using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32;
using System.Threading.Tasks;

namespace TNDexplorer.Core
{
    public class NetworkDriveInfo
    {
        public string DriveLetter { get; set; }
        public string UncPath { get; set; }
        public string VolumeName { get; set; }
        public long TotalSize { get; set; }
        public long FreeSpace { get; set; }

        public string DisplayName
        {
            get
            {
                string name = !string.IsNullOrEmpty(VolumeName) ? VolumeName : UncPath;
                return $"{DriveLetter} ({name})";
            }
        }

        public string SizeDisplay
        {
            get
            {
                if (TotalSize <= 0) return "";
                return $"{FormatSize(TotalSize - FreeSpace)} / {FormatSize(TotalSize)}";
            }
        }

        private static string FormatSize(long bytes)
        {
            if (bytes >= 1L << 40) return $"{bytes / (double)(1L << 40):F1} TB";
            if (bytes >= 1L << 30) return $"{bytes / (double)(1L << 30):F1} GB";
            if (bytes >= 1L << 20) return $"{bytes / (double)(1L << 20):F1} MB";
            return $"{bytes / (double)(1L << 10):F1} KB";
        }
    }

    public class LocalDriveInfo
    {
        public string DriveLetter { get; set; }
        public string VolumeLabel { get; set; }
        public long TotalSize { get; set; }
        public long FreeSpace { get; set; }
        public string DriveFormat { get; set; }

        public string DisplayName
        {
            get
            {
                string label = !string.IsNullOrEmpty(VolumeLabel) ? VolumeLabel : "ローカルディスク";
                return $"{DriveLetter} ({label})";
            }
        }

        public string SizeDisplay
        {
            get
            {
                if (TotalSize <= 0) return "";
                return $"{FormatSize(TotalSize - FreeSpace)} / {FormatSize(TotalSize)}";
            }
        }

        private static string FormatSize(long bytes)
        {
            if (bytes >= 1L << 40) return $"{bytes / (double)(1L << 40):F1} TB";
            if (bytes >= 1L << 30) return $"{bytes / (double)(1L << 30):F1} GB";
            if (bytes >= 1L << 20) return $"{bytes / (double)(1L << 20):F1} MB";
            return $"{bytes / (double)(1L << 10):F1} KB";
        }
    }

    public enum DriveChangeType
    {
        Added,
        Removed,
    }

    public class DriveChangeEvent
    {
        public string DriveLetter { get; set; }
        public DriveChangeType ChangeType { get; set; }
    }

    public class NetworkDriveDetector
    {
        // ===== Static drive letter ↔ UNC path mapping cache =====
        // Built at startup via non-elevated user token (net use).
        // Used by GetUncPath() as fallback when WNetGetConnection and registry both fail
        // (e.g., VBS logon script creates non-persistent mappings invisible to admin token).
        private static readonly ConcurrentDictionary<string, string> _driveToUncCache
            = new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private static readonly ConcurrentDictionary<string, string> _uncToDriveCache
            = new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        private static void LogDriveMapping(string message)
        {
            TNDexplorer.Services.AppLog.Info("DriveMapping", message);
        }

        /// <summary>
        /// Build the drive letter ↔ UNC path mapping cache by running "net use"
        /// as the non-elevated (linked) user token. This captures drive mappings
        /// created by VBS logon scripts that are invisible to the admin-elevated process.
        /// Call once at app startup.
        /// </summary>
        public void BuildDriveLetterCache()
        {
            LogDriveMapping("=== BuildDriveLetterCache START ===");

            // Step 1: Run "net use" as the non-elevated (linked) user token
            try
            {
                LogDriveMapping("Step 1: ParseNetUseOutputAsUser - attempting linked token approach...");
                var userDrives = ParseNetUseOutputAsUser();
                LogDriveMapping($"Step 1: ParseNetUseOutputAsUser returned {userDrives.Count} drive(s)");
                foreach (var d in userDrives)
                {
                    LogDriveMapping($"  LinkedToken drive: {d.DriveLetter} -> {d.UncPath}");
                    if (!string.IsNullOrEmpty(d.DriveLetter) && !string.IsNullOrEmpty(d.UncPath))
                    {
                        _driveToUncCache[d.DriveLetter] = d.UncPath;
                        string uncNorm = d.UncPath.TrimEnd('\\');
                        _uncToDriveCache[uncNorm] = d.DriveLetter;
                    }
                }
            }
            catch (Exception ex)
            {
                LogDriveMapping($"Step 1: ParseNetUseOutputAsUser FAILED: {ex.GetType().Name}: {ex.Message}");
                Debug.WriteLine($"BuildDriveLetterCache failed: {ex.Message}");
            }

            // Step 2: Merge results from other detection methods (WMI, DriveInfo, etc.)
            try
            {
                LogDriveMapping("Step 2: DetectMappedDrives - merging other detection methods...");
                var allDrives = DetectMappedDrives();
                LogDriveMapping($"Step 2: DetectMappedDrives returned {allDrives.Count} drive(s)");
                foreach (var d in allDrives)
                {
                    LogDriveMapping($"  Detected drive: {d.DriveLetter} -> {d.UncPath}");
                    if (!string.IsNullOrEmpty(d.DriveLetter) && !string.IsNullOrEmpty(d.UncPath))
                    {
                        _driveToUncCache.TryAdd(d.DriveLetter, d.UncPath);
                        string uncNorm = d.UncPath.TrimEnd('\\');
                        _uncToDriveCache.TryAdd(uncNorm, d.DriveLetter);
                    }
                }
            }
            catch (Exception ex)
            {
                LogDriveMapping($"Step 2: DetectMappedDrives FAILED: {ex.GetType().Name}: {ex.Message}");
                Debug.WriteLine($"BuildDriveLetterCache (DetectMappedDrives) failed: {ex.Message}");
            }

            // Summary
            LogDriveMapping($"=== BuildDriveLetterCache DONE: {_driveToUncCache.Count} mapping(s) in cache ===");
            foreach (var kvp in _driveToUncCache)
            {
                LogDriveMapping($"  Cache entry: {kvp.Key} -> {kvp.Value}");
            }

            // Acquire and cache a user token for impersonation during network scans.
            // This token is kept for the process lifetime so that Task.Run workers
            // can call ImpersonateLoggedOnUser without re-acquiring each time.
            AcquireUserToken();
        }

        /// <summary>
        /// Look up a UNC path from the startup-time mapping cache.
        /// Returns null if not found.
        /// </summary>
        public static string GetUncPathFromCache(string driveLetter)
        {
            if (string.IsNullOrEmpty(driveLetter))
                return null;

            // Normalize to "K:" format
            string key = driveLetter.Length >= 2 ? driveLetter.Substring(0, 2).ToUpper() : driveLetter.ToUpper();
            _driveToUncCache.TryGetValue(key, out string unc);
            return unc;
        }

        /// <summary>
        /// Look up a drive letter from a UNC path using the startup-time mapping cache.
        /// Returns null if not found.
        /// </summary>
        public static string GetDriveLetterFromCache(string uncPath)
        {
            if (string.IsNullOrEmpty(uncPath))
                return null;

            string uncNorm = uncPath.TrimEnd('\\');
            _uncToDriveCache.TryGetValue(uncNorm, out string drive);
            return drive;
        }

        // Win32 API for WNetGetConnection (works even under admin elevation)
        [DllImport("mpr.dll", CharSet = CharSet.Unicode)]
        private static extern int WNetGetConnection(string lpLocalName,
            System.Text.StringBuilder lpRemoteName, ref int lpnLength);

        // APIs for running child process as the non-elevated (linked) user token
        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool OpenProcessToken(IntPtr ProcessHandle,
            uint DesiredAccess, out IntPtr TokenHandle);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool GetTokenInformation(IntPtr TokenHandle,
            int TokenInformationClass, out IntPtr TokenInformation,
            int TokenInformationLength, out int ReturnLength);

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool DuplicateTokenEx(IntPtr hExistingToken,
            uint dwDesiredAccess, IntPtr lpTokenAttributes,
            int ImpersonationLevel, int TokenType, out IntPtr phNewToken);

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool CreateProcessAsUser(
            IntPtr hToken,
            string lpApplicationName,
            string lpCommandLine,
            IntPtr lpProcessAttributes,
            IntPtr lpThreadAttributes,
            bool bInheritHandles,
            uint dwCreationFlags,
            IntPtr lpEnvironment,
            string lpCurrentDirectory,
            ref STARTUPINFO lpStartupInfo,
            out PROCESS_INFORMATION lpProcessInformation);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr hObject);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr GetCurrentProcess();

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint WaitForSingleObject(IntPtr hHandle, uint dwMilliseconds);

        [DllImport("kernel32.dll")]
        private static extern bool CreatePipe(out IntPtr hReadPipe, out IntPtr hWritePipe,
            ref SECURITY_ATTRIBUTES lpPipeAttributes, uint nSize);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetHandleInformation(IntPtr hObject, uint dwMask, uint dwFlags);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool ReadFile(IntPtr hFile, byte[] lpBuffer,
            uint nNumberOfBytesToRead, out uint lpNumberOfBytesRead, IntPtr lpOverlapped);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, int dwProcessId);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct STARTUPINFO
        {
            public int cb;
            public string lpReserved;
            public string lpDesktop;
            public string lpTitle;
            public int dwX, dwY, dwXSize, dwYSize;
            public int dwXCountChars, dwYCountChars;
            public int dwFillAttribute;
            public int dwFlags;
            public short wShowWindow;
            public short cbReserved2;
            public IntPtr lpReserved2;
            public IntPtr hStdInput;
            public IntPtr hStdOutput;
            public IntPtr hStdError;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct PROCESS_INFORMATION
        {
            public IntPtr hProcess;
            public IntPtr hThread;
            public int dwProcessId;
            public int dwThreadId;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct SECURITY_ATTRIBUTES
        {
            public int nLength;
            public IntPtr lpSecurityDescriptor;
            public bool bInheritHandle;
        }

        private const uint TOKEN_QUERY = 0x0008;
        private const uint TOKEN_DUPLICATE = 0x0002;
        private const uint TOKEN_ASSIGN_PRIMARY = 0x0001;
        private const uint MAXIMUM_ALLOWED = 0x02000000;
        private const int TokenLinkedToken = 19;
        private const int SecurityImpersonation = 2;
        private const int TokenPrimary = 1;
        private const uint CREATE_NO_WINDOW = 0x08000000;
        private const int STARTF_USESTDHANDLES = 0x00000100;
        private const uint HANDLE_FLAG_INHERIT = 0x00000001;
        private const uint PROCESS_QUERY_INFORMATION = 0x0400;

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool ImpersonateLoggedOnUser(IntPtr hToken);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool RevertToSelf();

        // Once set to true, ParseNetUseOutputAsUser() returns empty immediately
        // to avoid repeated Win32 API calls that are known to fail in this environment.
        private static bool _userTokenUnavailable = false;

        // Long-lived user token for impersonation during network drive scanning.
        // Acquired once at startup, reused for all scan tasks. Closed on Dispose.
        private static IntPtr _cachedUserToken = IntPtr.Zero;
        private static readonly object _tokenLock = new object();

        public List<NetworkDriveInfo> DetectMappedDrives()
        {
            var drives = new List<NetworkDriveInfo>();
            var foundLetters = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // Method 1: WMI (works for drives visible to current session)
            try
            {
                using (var searcher = new ManagementObjectSearcher(
                    "SELECT DeviceID, ProviderName, VolumeName, Size, FreeSpace FROM Win32_LogicalDisk WHERE DriveType = 4"))
                {
                    foreach (ManagementObject mo in searcher.Get())
                    {
                        string letter = mo["DeviceID"]?.ToString();
                        if (string.IsNullOrEmpty(letter)) continue;

                        foundLetters.Add(letter);
                        drives.Add(new NetworkDriveInfo
                        {
                            DriveLetter = letter,
                            UncPath = mo["ProviderName"]?.ToString() ?? "",
                            VolumeName = mo["VolumeName"]?.ToString() ?? "",
                            TotalSize = Convert.ToInt64(mo["Size"] ?? 0),
                            FreeSpace = Convert.ToInt64(mo["FreeSpace"] ?? 0)
                        });
                    }
                }
            }
            catch { }

            // Method 2: DriveInfo.GetDrives for Network type
            try
            {
                foreach (var di in DriveInfo.GetDrives())
                {
                    if (di.DriveType == DriveType.Network)
                    {
                        string letter = di.Name.TrimEnd('\\').TrimEnd('/');
                        if (foundLetters.Contains(letter)) continue;

                        foundLetters.Add(letter);
                        string unc = GetUncPath(letter);
                        long totalSize = 0;
                        long freeSpace = 0;
                        string volName = "";
                        try
                        {
                            if (di.IsReady)
                            {
                                totalSize = di.TotalSize;
                                freeSpace = di.TotalFreeSpace;
                                volName = di.VolumeLabel;
                            }
                        }
                        catch { }

                        drives.Add(new NetworkDriveInfo
                        {
                            DriveLetter = letter,
                            UncPath = unc ?? "",
                            VolumeName = volName,
                            TotalSize = totalSize,
                            FreeSpace = freeSpace,
                        });
                    }
                }
            }
            catch { }

            // Method 3: WNetGetConnection - scan all drive letters
            // Catches drives mapped in user sessions not visible under admin elevation
            try
            {
                for (char c = 'A'; c <= 'Z'; c++)
                {
                    string letter = c + ":";
                    if (foundLetters.Contains(letter)) continue;

                    string unc = GetUncPath(letter);
                    if (!string.IsNullOrEmpty(unc))
                    {
                        foundLetters.Add(letter);
                        long totalSize = 0;
                        long freeSpace = 0;
                        try
                        {
                            var di = new DriveInfo(letter);
                            if (di.IsReady)
                            {
                                totalSize = di.TotalSize;
                                freeSpace = di.TotalFreeSpace;
                            }
                        }
                        catch { }

                        drives.Add(new NetworkDriveInfo
                        {
                            DriveLetter = letter,
                            UncPath = unc,
                            TotalSize = totalSize,
                            FreeSpace = freeSpace,
                        });
                    }
                }
            }
            catch { }

            // Method 4: Registry HKCU\Network\<DriveLetter> (most reliable under admin elevation)
            // User-mapped drives are stored here regardless of session, surviving UAC elevation
            try
            {
                var regDrives = ReadNetworkDrivesFromRegistry();
                foreach (var rd in regDrives)
                {
                    if (foundLetters.Contains(rd.DriveLetter)) continue;
                    foundLetters.Add(rd.DriveLetter);
                    drives.Add(rd);
                }
            }
            catch { }

            // Method 5: Parse "net use" output (runs as admin - sees admin session drives)
            try
            {
                var netUseDrives = ParseNetUseOutput();
                foreach (var nud in netUseDrives)
                {
                    if (foundLetters.Contains(nud.DriveLetter)) continue;
                    foundLetters.Add(nud.DriveLetter);
                    drives.Add(nud);
                }
            }
            catch { }

            // Method 6: User-session drives from startup cache (built by BuildDriveLetterCache).
            // No need to re-call ParseNetUseOutputAsUser() here — the results are already
            // in the static _driveToUncCache and will be picked up by GetUncPath() in Method 3.

            return drives;
        }

        /// <summary>
        /// Resolve a mapped network drive letter (e.g., "K:") to its UNC path.
        /// Tries 3 methods in order:
        ///   1. WNetGetConnection API (current token's session)
        ///   2. HKCU\Network registry (persistent mappings)
        ///   3. Startup-time cache built from non-elevated "net use" (VBS logon script mappings)
        /// Returns null if the drive is not a network drive or all methods fail.
        /// </summary>
        public static string GetUncPath(string driveLetter)
        {
            // Method 1: WNetGetConnection (works for drives in current token's session)
            try
            {
                var sb = new System.Text.StringBuilder(512);
                int length = sb.Capacity;
                int result = WNetGetConnection(driveLetter, sb, ref length);
                if (result == 0) // NO_ERROR
                    return sb.ToString();
            }
            catch { }

            // Method 2: Registry HKCU\Network\{letter}\RemotePath
            string regResult = GetUncPathFromRegistry(driveLetter);
            if (regResult != null)
                return regResult;

            // Method 3: Startup-time cache (populated from non-elevated user's "net use")
            // Catches VBS logon script mappings that are non-persistent (no HKCU\Network entry)
            string cacheResult = GetUncPathFromCache(driveLetter);
            if (cacheResult != null)
                return cacheResult;

            return null;
        }

        /// <summary>
        /// Read UNC path for a drive letter from HKCU\Network\{letter}\RemotePath.
        /// This works reliably from UAC-elevated processes because HKCU is shared
        /// between the elevated and non-elevated tokens of the same user.
        /// </summary>
        public static string GetUncPathFromRegistry(string driveLetter)
        {
            if (string.IsNullOrEmpty(driveLetter) || driveLetter.Length < 1)
                return null;

            try
            {
                char letter = char.ToUpper(driveLetter[0]);
                if (!char.IsLetter(letter))
                    return null;

                using (var key = Registry.CurrentUser.OpenSubKey(@"Network\" + letter))
                {
                    string remotePath = key?.GetValue("RemotePath")?.ToString();
                    if (!string.IsNullOrEmpty(remotePath))
                        return remotePath;
                }
            }
            catch { }

            return null;
        }

        /// <summary>
        /// Reads network drive mappings from HKCU\Network\{DriveLetter}.
        /// This registry location stores persistent drive mappings for the current user,
        /// and is readable from admin-elevated processes regardless of session.
        /// Each subkey (e.g., "Z") contains:
        ///   RemotePath (REG_SZ): UNC path like \\server\share
        ///   ProviderName (REG_SZ): Network provider (e.g., "Microsoft Windows Network")
        /// </summary>
        private static List<NetworkDriveInfo> ReadNetworkDrivesFromRegistry()
        {
            var drives = new List<NetworkDriveInfo>();

            try
            {
                using (var networkKey = Registry.CurrentUser.OpenSubKey(@"Network"))
                {
                    if (networkKey == null) return drives;

                    foreach (string subKeyName in networkKey.GetSubKeyNames())
                    {
                        // Each subkey name is a single letter (e.g., "Z")
                        if (string.IsNullOrEmpty(subKeyName) || subKeyName.Length != 1 || !char.IsLetter(subKeyName[0]))
                            continue;

                        try
                        {
                            using (var driveKey = networkKey.OpenSubKey(subKeyName))
                            {
                                if (driveKey == null) continue;

                                string remotePath = driveKey.GetValue("RemotePath")?.ToString();
                                if (string.IsNullOrEmpty(remotePath)) continue;

                                string driveLetter = subKeyName.ToUpper() + ":";
                                long totalSize = 0, freeSpace = 0;
                                string volName = "";

                                try
                                {
                                    var di = new DriveInfo(driveLetter);
                                    if (di.IsReady)
                                    {
                                        totalSize = di.TotalSize;
                                        freeSpace = di.TotalFreeSpace;
                                        volName = di.VolumeLabel;
                                    }
                                }
                                catch { }

                                drives.Add(new NetworkDriveInfo
                                {
                                    DriveLetter = driveLetter,
                                    UncPath = remotePath,
                                    VolumeName = volName,
                                    TotalSize = totalSize,
                                    FreeSpace = freeSpace,
                                });
                            }
                        }
                        catch { }
                    }
                }
            }
            catch { }

            return drives;
        }

        private static List<NetworkDriveInfo> ParseNetUseOutput()
        {
            var drives = new List<NetworkDriveInfo>();

            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "net",
                    Arguments = "use",
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };

                using (var proc = Process.Start(psi))
                {
                    if (proc == null) return drives;
                    string output = proc.StandardOutput.ReadToEnd();
                    proc.WaitForExit(5000);

                    var lines = output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                    foreach (var line in lines)
                    {
                        string trimmed = line.Trim();
                        if (trimmed.StartsWith("-") || trimmed.StartsWith("新しい")
                            || trimmed.StartsWith("The command")
                            || trimmed.StartsWith("Status")
                            || trimmed.StartsWith("ステータス")
                            || trimmed.StartsWith("コマンド"))
                            continue;

                        string driveLetter = null;
                        string uncPath = null;

                        var parts = trimmed.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                        foreach (var part in parts)
                        {
                            if (part.Length == 2 && char.IsLetter(part[0]) && part[1] == ':')
                                driveLetter = part.ToUpper();
                            else if (part.StartsWith("\\\\"))
                                uncPath = part;
                        }

                        if (driveLetter != null && uncPath != null)
                        {
                            long totalSize = 0, freeSpace = 0;
                            try
                            {
                                var di = new DriveInfo(driveLetter);
                                if (di.IsReady)
                                {
                                    totalSize = di.TotalSize;
                                    freeSpace = di.TotalFreeSpace;
                                }
                            }
                            catch { }

                            drives.Add(new NetworkDriveInfo
                            {
                                DriveLetter = driveLetter,
                                UncPath = uncPath,
                                TotalSize = totalSize,
                                FreeSpace = freeSpace,
                            });
                        }
                    }
                }
            }
            catch { }

            return drives;
        }

        /// <summary>
        /// Obtains the linked (non-elevated) token from the current admin process,
        /// then impersonates the standard user to query drive mappings via WNetGetConnection.
        /// This reveals network drives mapped in the user's standard session that are
        /// invisible to the admin-elevated token due to Windows UAC token split.
        ///
        /// Previous approach used CreateProcessAsUser to run "net use", but that requires
        /// SE_ASSIGNPRIMARYTOKEN_NAME privilege which admin-elevated processes lack in
        /// many corporate environments (Win32 error 1314).
        /// Current approach uses ImpersonateLoggedOnUser which only needs SE_IMPERSONATE_NAME
        /// (available to all administrators by default).
        /// </summary>
        private List<NetworkDriveInfo> ParseNetUseOutputAsUser()
        {
            var drives = new List<NetworkDriveInfo>();

            // Skip if previous attempts determined user token is permanently unavailable
            if (_userTokenUnavailable)
                return drives;

            IntPtr processToken = IntPtr.Zero;
            IntPtr linkedToken = IntPtr.Zero;
            IntPtr primaryToken = IntPtr.Zero;

            try
            {
                // Step 1: Open current process token
                if (!OpenProcessToken(GetCurrentProcess(),
                    TOKEN_QUERY | TOKEN_DUPLICATE, out processToken))
                {
                    int err = Marshal.GetLastWin32Error();
                    LogDriveMapping($"  ParseNetUseOutputAsUser: OpenProcessToken FAILED (Win32 error {err})");
                    _userTokenUnavailable = true;
                    return drives;
                }
                LogDriveMapping("  ParseNetUseOutputAsUser: OpenProcessToken OK");

                // Step 2: Get the linked (non-elevated) token
                int returnLength;
                if (!GetTokenInformation(processToken, TokenLinkedToken,
                    out linkedToken, IntPtr.Size, out returnLength))
                {
                    int err = Marshal.GetLastWin32Error();
                    LogDriveMapping($"  ParseNetUseOutputAsUser: GetTokenInformation(LinkedToken) FAILED (Win32 error {err})");
                    _userTokenUnavailable = true;
                    return drives;
                }
                LogDriveMapping($"  ParseNetUseOutputAsUser: GetTokenInformation OK (linkedToken=0x{linkedToken.ToInt64():X})");

                if (linkedToken == IntPtr.Zero)
                {
                    LogDriveMapping("  ParseNetUseOutputAsUser: linkedToken is NULL - no linked token available");
                    _userTokenUnavailable = true;
                    return drives;
                }

                // Step 3: Duplicate as primary token
                if (!DuplicateTokenEx(linkedToken, MAXIMUM_ALLOWED, IntPtr.Zero,
                    SecurityImpersonation, TokenPrimary, out primaryToken))
                {
                    int err = Marshal.GetLastWin32Error();
                    LogDriveMapping($"  ParseNetUseOutputAsUser: DuplicateTokenEx FAILED (Win32 error {err})");

                    // Fallback: Get a primary token from the user's explorer.exe process.
                    // The linked token from GetTokenInformation(TokenLinkedToken) is returned at
                    // SecurityIdentification level by Windows design, making DuplicateTokenEx fail
                    // with ERROR_BAD_TOKEN_TYPE (1346). Explorer.exe runs as the non-elevated user
                    // with a full primary token that can be duplicated successfully.
                    primaryToken = TryGetTokenFromExplorer();
                    if (primaryToken == IntPtr.Zero)
                    {
                        LogDriveMapping("  ParseNetUseOutputAsUser: All token approaches failed");
                        _userTokenUnavailable = true;
                        return drives;
                    }
                }
                else
                {
                    LogDriveMapping("  ParseNetUseOutputAsUser: DuplicateTokenEx OK");
                }

                // Step 4: Impersonate the user and query drive mappings via WNetGetConnection.
                // ImpersonateLoggedOnUser requires SE_IMPERSONATE_NAME privilege (admin processes
                // have this by default), unlike CreateProcessAsUser which requires
                // SE_ASSIGNPRIMARYTOKEN_NAME (typically only available to services).
                if (!ImpersonateLoggedOnUser(primaryToken))
                {
                    int err = Marshal.GetLastWin32Error();
                    LogDriveMapping($"  ParseNetUseOutputAsUser: ImpersonateLoggedOnUser FAILED (Win32 error {err})");
                    _userTokenUnavailable = true;
                    return drives;
                }
                LogDriveMapping("  ParseNetUseOutputAsUser: ImpersonateLoggedOnUser OK");

                try
                {
                    // Scan all drive letters for network mappings under the impersonated user context.
                    // WNetGetConnection uses the thread's effective token (impersonation token),
                    // which carries the user's logon session LUID where VBS script mappings exist.
                    for (char c = 'A'; c <= 'Z'; c++)
                    {
                        string driveLetter = c + ":";
                        try
                        {
                            var sb = new System.Text.StringBuilder(512);
                            int length = sb.Capacity;
                            int result = WNetGetConnection(driveLetter, sb, ref length);
                            if (result == 0) // NO_ERROR
                            {
                                string uncPath = sb.ToString();
                                long totalSize = 0, freeSpace = 0;
                                try
                                {
                                    var di = new DriveInfo(driveLetter);
                                    if (di.IsReady)
                                    {
                                        totalSize = di.TotalSize;
                                        freeSpace = di.TotalFreeSpace;
                                    }
                                }
                                catch { }

                                drives.Add(new NetworkDriveInfo
                                {
                                    DriveLetter = driveLetter,
                                    UncPath = uncPath,
                                    TotalSize = totalSize,
                                    FreeSpace = freeSpace,
                                });
                                LogDriveMapping($"  ParseNetUseOutputAsUser: Found {driveLetter} -> {uncPath}");
                            }
                        }
                        catch { }
                    }
                    LogDriveMapping($"  ParseNetUseOutputAsUser: Impersonation scan found {drives.Count} drive(s)");
                }
                finally
                {
                    RevertToSelf();
                    LogDriveMapping("  ParseNetUseOutputAsUser: RevertToSelf done");
                }
            }
            catch (Exception ex)
            {
                LogDriveMapping($"  ParseNetUseOutputAsUser: EXCEPTION: {ex.GetType().Name}: {ex.Message}");
            }
            finally
            {
                if (primaryToken != IntPtr.Zero) CloseHandle(primaryToken);
                if (linkedToken != IntPtr.Zero) CloseHandle(linkedToken);
                if (processToken != IntPtr.Zero) CloseHandle(processToken);
            }

            return drives;
        }

        /// <summary>
        /// Fallback: Obtain a usable primary token from the user's explorer.exe process.
        /// Explorer.exe runs as the non-elevated user with a full primary token,
        /// unlike the linked token from GetTokenInformation(TokenLinkedToken) which
        /// is restricted to SecurityIdentification level.
        /// </summary>
        private IntPtr TryGetTokenFromExplorer()
        {
            LogDriveMapping("  TryGetTokenFromExplorer: Searching for explorer.exe...");
            try
            {
                var explorers = Process.GetProcessesByName("explorer");
                LogDriveMapping($"  TryGetTokenFromExplorer: Found {explorers.Length} explorer.exe process(es)");

                foreach (var explorer in explorers)
                {
                    try
                    {
                        IntPtr hProcess = OpenProcess(PROCESS_QUERY_INFORMATION, false, explorer.Id);
                        if (hProcess == IntPtr.Zero)
                        {
                            int err = Marshal.GetLastWin32Error();
                            LogDriveMapping($"  TryGetTokenFromExplorer: OpenProcess(PID={explorer.Id}) FAILED (Win32 error {err})");
                            continue;
                        }

                        IntPtr explorerToken = IntPtr.Zero;
                        try
                        {
                            if (!OpenProcessToken(hProcess, TOKEN_DUPLICATE | TOKEN_QUERY, out explorerToken))
                            {
                                int err = Marshal.GetLastWin32Error();
                                LogDriveMapping($"  TryGetTokenFromExplorer: OpenProcessToken(PID={explorer.Id}) FAILED (Win32 error {err})");
                                continue;
                            }

                            IntPtr primaryToken;
                            if (DuplicateTokenEx(explorerToken, MAXIMUM_ALLOWED, IntPtr.Zero,
                                SecurityImpersonation, TokenPrimary, out primaryToken))
                            {
                                LogDriveMapping($"  TryGetTokenFromExplorer: DuplicateTokenEx OK from explorer.exe (PID={explorer.Id})");
                                return primaryToken;
                            }
                            else
                            {
                                int err = Marshal.GetLastWin32Error();
                                LogDriveMapping($"  TryGetTokenFromExplorer: DuplicateTokenEx(PID={explorer.Id}) FAILED (Win32 error {err})");
                            }
                        }
                        finally
                        {
                            if (explorerToken != IntPtr.Zero) CloseHandle(explorerToken);
                            CloseHandle(hProcess);
                        }
                    }
                    catch (Exception ex)
                    {
                        LogDriveMapping($"  TryGetTokenFromExplorer: Exception for PID={explorer.Id}: {ex.Message}");
                    }
                    finally
                    {
                        explorer.Dispose();
                    }
                }
            }
            catch (Exception ex)
            {
                LogDriveMapping($"  TryGetTokenFromExplorer: EXCEPTION: {ex.GetType().Name}: {ex.Message}");
            }

            LogDriveMapping("  TryGetTokenFromExplorer: No usable token found from any explorer.exe");
            return IntPtr.Zero;
        }

        public List<LocalDriveInfo> DetectLocalNtfsDrives()
        {
            var drives = new List<LocalDriveInfo>();

            foreach (var driveInfo in DriveInfo.GetDrives())
            {
                try
                {
                    if (driveInfo.IsReady
                        && driveInfo.DriveType == DriveType.Fixed
                        && string.Equals(driveInfo.DriveFormat, "NTFS", StringComparison.OrdinalIgnoreCase))
                    {
                        drives.Add(new LocalDriveInfo
                        {
                            DriveLetter = driveInfo.Name.TrimEnd('\\').TrimEnd('/'),
                            VolumeLabel = driveInfo.VolumeLabel,
                            TotalSize = driveInfo.TotalSize,
                            FreeSpace = driveInfo.TotalFreeSpace,
                            DriveFormat = driveInfo.DriveFormat
                        });
                    }
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }

            return drives;
        }

        public async Task MonitorDriveChanges(
            Action<DriveChangeEvent> onChange,
            TimeSpan checkInterval,
            CancellationToken ct)
        {
            var knownDrives = new HashSet<string>(
                DetectMappedDrives().Select(d => d.DriveLetter));

            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(checkInterval, ct).ConfigureAwait(false);
                if (ct.IsCancellationRequested) break;

                var currentDrives = new HashSet<string>(
                    DetectMappedDrives().Select(d => d.DriveLetter));

                foreach (var drive in currentDrives.Except(knownDrives))
                {
                    onChange?.Invoke(new DriveChangeEvent
                    {
                        DriveLetter = drive,
                        ChangeType = DriveChangeType.Added
                    });
                }

                foreach (var drive in knownDrives.Except(currentDrives))
                {
                    onChange?.Invoke(new DriveChangeEvent
                    {
                        DriveLetter = drive,
                        ChangeType = DriveChangeType.Removed
                    });
                }

                knownDrives = currentDrives;
            }
        }

        /// <summary>
        /// Acquire a long-lived user (non-elevated) token and cache it for impersonation.
        /// Uses the same linked-token / explorer-token fallback as ParseNetUseOutputAsUser.
        /// </summary>
        private void AcquireUserToken()
        {
            if (_userTokenUnavailable) return;

            lock (_tokenLock)
            {
                if (_cachedUserToken != IntPtr.Zero) return; // Already acquired

                IntPtr processToken = IntPtr.Zero;
                IntPtr linkedToken = IntPtr.Zero;

                try
                {
                    if (!OpenProcessToken(GetCurrentProcess(),
                        TOKEN_QUERY | TOKEN_DUPLICATE, out processToken))
                    {
                        LogDriveMapping("AcquireUserToken: OpenProcessToken FAILED");
                        return;
                    }

                    int returnLength;
                    if (!GetTokenInformation(processToken, TokenLinkedToken,
                        out linkedToken, IntPtr.Size, out returnLength)
                        || linkedToken == IntPtr.Zero)
                    {
                        LogDriveMapping("AcquireUserToken: GetTokenInformation(LinkedToken) FAILED or NULL");
                        // Fall through to explorer fallback
                    }
                    else
                    {
                        IntPtr primary;
                        if (DuplicateTokenEx(linkedToken, MAXIMUM_ALLOWED, IntPtr.Zero,
                            SecurityImpersonation, TokenPrimary, out primary))
                        {
                            _cachedUserToken = primary;
                            LogDriveMapping("AcquireUserToken: Cached via linked token");
                            return;
                        }
                        LogDriveMapping("AcquireUserToken: DuplicateTokenEx from linked token FAILED, trying explorer");
                    }

                    // Fallback: explorer.exe token
                    IntPtr explorerToken = TryGetTokenFromExplorer();
                    if (explorerToken != IntPtr.Zero)
                    {
                        _cachedUserToken = explorerToken;
                        LogDriveMapping("AcquireUserToken: Cached via explorer.exe token");
                    }
                    else
                    {
                        LogDriveMapping("AcquireUserToken: All approaches failed — network scans will run as admin");
                    }
                }
                finally
                {
                    if (linkedToken != IntPtr.Zero) CloseHandle(linkedToken);
                    if (processToken != IntPtr.Zero) CloseHandle(processToken);
                }
            }
        }

        /// <summary>
        /// Execute an action under the non-elevated user's impersonation context.
        /// If no user token is available, the action runs as-is (admin context).
        /// Thread-safe: each call pairs ImpersonateLoggedOnUser with RevertToSelf
        /// in a try/finally to prevent token leakage on ThreadPool threads.
        /// </summary>
        public static void RunImpersonated(Action action)
        {
            IntPtr token = _cachedUserToken;
            if (token == IntPtr.Zero)
            {
                // No user token available — run without impersonation
                action();
                return;
            }

            if (!ImpersonateLoggedOnUser(token))
            {
                int err = Marshal.GetLastWin32Error();
                Debug.WriteLine($"RunImpersonated: ImpersonateLoggedOnUser failed (Win32 error {err})");
                // Fallback: run without impersonation
                action();
                return;
            }

            try
            {
                action();
            }
            finally
            {
                RevertToSelf();
            }
        }

        /// <summary>
        /// Release the cached user token. Call on application shutdown.
        /// </summary>
        public static void ReleaseUserToken()
        {
            lock (_tokenLock)
            {
                if (_cachedUserToken != IntPtr.Zero)
                {
                    CloseHandle(_cachedUserToken);
                    _cachedUserToken = IntPtr.Zero;
                    LogDriveMapping("ReleaseUserToken: Cached token released");
                }
            }
        }
    }
}
