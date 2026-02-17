using System;
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
        // Win32 API for WNetGetConnection (works even under admin elevation)
        [DllImport("mpr.dll", CharSet = CharSet.Unicode)]
        private static extern int WNetGetConnection(string lpLocalName,
            System.Text.StringBuilder lpRemoteName, ref int lpnLength);

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

            // Method 5: Parse "net use" output as last resort
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

            return drives;
        }

        private static string GetUncPath(string driveLetter)
        {
            try
            {
                var sb = new System.Text.StringBuilder(512);
                int length = sb.Capacity;
                int result = WNetGetConnection(driveLetter, sb, ref length);
                if (result == 0) // NO_ERROR
                    return sb.ToString();
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
    }
}
