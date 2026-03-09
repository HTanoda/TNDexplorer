using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using Microsoft.Win32;

namespace TNDexplorer.Core
{
    /// <summary>
    /// WPF host for IPreviewHandler COM objects.
    /// Creates a dedicated child HWND for hosting the preview content.
    ///
    /// PDF files are handled specially via PdfRenderer (WinRT COM interop)
    /// because Adobe's IPreviewHandler rejects admin-elevated processes.
    /// </summary>
    public class PreviewHandlerHost : IDisposable
    {
        private IPreviewHandler _currentHandler;
        private IntPtr _hostHwnd;
        private bool _disposed;
        private FileStreamWrapper _currentStream;

        private const int WS_CHILD = 0x40000000;
        private const int WS_VISIBLE = 0x10000000;
        private const int WS_CLIPCHILDREN = 0x02000000;

        private const uint CLSCTX_INPROC_SERVER = 0x1;
        private const uint CLSCTX_INPROC_HANDLER = 0x2;
        private const uint CLSCTX_LOCAL_SERVER = 0x4;
        private const uint CLSCTX_ALL = CLSCTX_INPROC_SERVER | CLSCTX_INPROC_HANDLER | CLSCTX_LOCAL_SERVER;

        #region P/Invoke declarations

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr CreateWindowEx(
            int dwExStyle, string lpClassName, string lpWindowName,
            int dwStyle, int x, int y, int nWidth, int nHeight,
            IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DestroyWindow(IntPtr hwnd);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool MoveWindow(IntPtr hwnd, int x, int y, int nWidth, int nHeight, bool bRepaint);

        [DllImport("ole32.dll")]
        private static extern int CoCreateInstance(
            [In] ref Guid rclsid,
            IntPtr pUnkOuter,
            uint dwClsContext,
            [In] ref Guid riid,
            out IntPtr ppv);

        // Medium IL impersonation for admin-elevated COM interop
        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool OpenProcessToken(IntPtr processHandle, uint desiredAccess, out IntPtr tokenHandle);

        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DuplicateTokenEx(
            IntPtr hExistingToken, uint dwDesiredAccess,
            IntPtr lpTokenAttributes, int impersonationLevel,
            int tokenType, out IntPtr phNewToken);

        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetTokenInformation(
            IntPtr tokenHandle, int tokenInformationClass,
            IntPtr tokenInformation, uint tokenInformationLength);

        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool ImpersonateLoggedOnUser(IntPtr hToken);

        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool RevertToSelf();

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr GetCurrentProcess();

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseHandle(IntPtr hObject);

        private const uint TOKEN_DUPLICATE = 0x0002;
        private const uint TOKEN_QUERY = 0x0008;
        private const uint TOKEN_ADJUST_DEFAULT = 0x0080;
        private const uint TOKEN_ASSIGN_PRIMARY = 0x0001;
        private const uint TOKEN_IMPERSONATE = 0x0004;
        private const uint MAXIMUM_ALLOWED = 0x02000000;
        private const int SecurityImpersonation = 2;
        private const int TokenImpersonation = 2;
        private const int TokenIntegrityLevel = 25;

        #endregion

        #region COM Interfaces

        [ComImport, Guid("8895b1c6-b41f-4c1c-a562-0d564250836f"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IPreviewHandler
        {
            void SetWindow(IntPtr hwnd, ref RECT rect);
            void SetRect(ref RECT rect);
            void DoPreview();
            void Unload();
            void SetFocus();
            void QueryFocus(out IntPtr phwnd);
            [PreserveSig]
            uint TranslateAccelerator(ref MSG pmsg);
        }

        [ComImport, Guid("b7d14566-0509-4cce-a71f-0a554233bd9b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IInitializeWithFile
        {
            void Initialize([MarshalAs(UnmanagedType.LPWStr)] string pszFilePath, uint grfMode);
        }

        [ComImport, Guid("b824b49d-22ac-4161-ac8a-9916e8fa3f7f"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IInitializeWithStream
        {
            void Initialize(IStream pstream, uint grfMode);
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct RECT
        {
            public int Left, Top, Right, Bottom;
            public RECT(int l, int t, int r, int b) { Left = l; Top = t; Right = r; Bottom = b; }
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct MSG
        {
            public IntPtr hwnd;
            public uint message;
            public IntPtr wParam;
            public IntPtr lParam;
            public uint time;
            public System.Drawing.Point pt;
        }

        #endregion

        private static readonly Guid IID_IUnknown = new Guid("00000000-0000-0000-C000-000000000046");

        // Cache of registered preview handler CLSIDs from HKLM
        private static HashSet<Guid> _registeredHandlers;

        /// <summary>
        /// Returns true if the file has a Zone.Identifier ADS indicating
        /// it was downloaded from the Internet (ZoneId=3 or 4).
        /// </summary>
        public static bool HasZoneIdentifier(string filePath)
        {
            try
            {
                // File.Exists() does not work reliably with NTFS ADS paths.
                // Open the Zone.Identifier ADS directly with FileStream.
                string zoneFile = filePath + ":Zone.Identifier";
                using (var fs = new FileStream(zoneFile, FileMode.Open, FileAccess.Read, FileShare.Read))
                using (var reader = new StreamReader(fs))
                {
                    string content = reader.ReadToEnd();
                    return content.Contains("ZoneId=3") || content.Contains("ZoneId=4");
                }
            }
            catch { }
            return false;
        }

        /// <summary>
        /// Returns true if the given file extension is a PDF.
        /// </summary>
        public static bool IsPdfFile(string filePath)
        {
            if (string.IsNullOrEmpty(filePath)) return false;
            return string.Equals(Path.GetExtension(filePath), ".pdf", StringComparison.OrdinalIgnoreCase);
        }

        public void UnloadPreview()
        {
            if (_currentHandler != null)
            {
                try { _currentHandler.Unload(); } catch { }
                try { Marshal.ReleaseComObject(_currentHandler); } catch { }
                _currentHandler = null;
            }

            if (_currentStream != null)
            {
                try { _currentStream.Dispose(); } catch { }
                _currentStream = null;
            }

            if (_hostHwnd != IntPtr.Zero)
            {
                DestroyWindow(_hostHwnd);
                _hostHwnd = IntPtr.Zero;
            }
        }

        /// <summary>
        /// Show preview of a file using IPreviewHandler COM.
        /// For PDF files, use PdfRenderer.RenderFirstPage instead (called from MainWindow).
        /// </summary>
        public bool ShowPreview(string filePath, IntPtr parentHwnd, int left, int top, int width, int height)
        {
            return ShowPreview(filePath, parentHwnd, left, top, width, height, out _);
        }

        public bool ShowPreview(string filePath, IntPtr parentHwnd, int left, int top, int width, int height, out string error)
        {
            error = null;
            UnloadPreview();

            if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
            {
                error = "ファイルが見つかりません";
                return false;
            }

            if (width <= 0 || height <= 0)
            {
                error = $"表示領域が不正です ({width}x{height})";
                return false;
            }

            string extension = Path.GetExtension(filePath);
            if (string.IsNullOrEmpty(extension))
            {
                error = "拡張子がありません";
                return false;
            }

            Guid? handlerClsid = GetPreviewHandlerCLSID(extension);
            if (!handlerClsid.HasValue)
            {
                error = $"{extension} のプレビューハンドラが登録されていません";
                return false;
            }

            // Try 1: Without impersonation.
            // LocalServer32 handlers (Office apps) work better without Medium IL
            // impersonation, especially when the Office app is already running.
            string firstError;
            if (TryShowPreviewCore(filePath, parentHwnd, left, top, width, height, handlerClsid.Value, false, out firstError))
                return true;

            // Clean up any partial state from failed first attempt
            UnloadPreview();

            // Try 2: With Medium IL impersonation.
            // InprocServer32 handlers (e.g. some PDF viewers) may require Medium IL
            // when our process runs as admin (High IL).
            if (TryShowPreviewCore(filePath, parentHwnd, left, top, width, height, handlerClsid.Value, true, out error))
                return true;

            // Both attempts failed — use the more informative error
            if (string.IsNullOrEmpty(error)) error = firstError;
            return false;
        }

        /// <summary>
        /// Core preview logic. Optionally impersonates Medium IL before COM operations.
        /// </summary>
        private bool TryShowPreviewCore(string filePath, IntPtr parentHwnd, int left, int top, int width, int height,
            Guid handlerClsid, bool impersonate, out string error)
        {
            error = null;
            IntPtr ilToken = impersonate ? BeginMediumILImpersonation() : IntPtr.Zero;
            try
            {
                object handler = CreateComObject(handlerClsid);
                if (handler == null)
                {
                    error = $"COMオブジェクト生成に失敗\n({handlerClsid})";
                    return false;
                }

                // Initialize: try stream first (avoids file locking), then file path
                bool initialized = InitializeHandler(handler, filePath);
                if (!initialized)
                {
                    error = string.IsNullOrEmpty(_initError)
                        ? "ハンドラの初期化に失敗しました"
                        : $"ハンドラの初期化に失敗しました\n{_initError}";
                    try { Marshal.ReleaseComObject(handler); } catch { }
                    return false;
                }

                _hostHwnd = CreateWindowEx(
                    0, "Static", "",
                    WS_CHILD | WS_VISIBLE | WS_CLIPCHILDREN,
                    left, top, width, height,
                    parentHwnd, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);

                if (_hostHwnd == IntPtr.Zero)
                {
                    error = "プレビューウィンドウの作成に失敗しました";
                    try { Marshal.ReleaseComObject(handler); } catch { }
                    return false;
                }

                IPreviewHandler previewHandler = GetPreviewHandlerInterface(handler);
                if (previewHandler == null)
                {
                    error = "IPreviewHandler の取得に失敗しました";
                    try { Marshal.ReleaseComObject(handler); } catch { }
                    DestroyWindow(_hostHwnd);
                    _hostHwnd = IntPtr.Zero;
                    return false;
                }

                _currentHandler = previewHandler;

                var rect = new RECT(0, 0, width, height);
                _currentHandler.SetWindow(_hostHwnd, ref rect);
                _currentHandler.DoPreview();

                return true;
            }
            catch (Exception ex)
            {
                error = $"例外: {ex.Message}\n(HRESULT: 0x{ex.HResult:X8})";
                UnloadPreview();
                return false;
            }
            finally
            {
                EndMediumILImpersonation(ilToken);
            }
        }

        private IPreviewHandler GetPreviewHandlerInterface(object handler)
        {
            // Try QueryInterface first (more reliable for COM proxies)
            try
            {
                IntPtr pUnk = Marshal.GetIUnknownForObject(handler);
                try
                {
                    Guid iidPreview = typeof(IPreviewHandler).GUID;
                    int qiHr = Marshal.QueryInterface(pUnk, ref iidPreview, out IntPtr pPreview);
                    if (qiHr == 0 && pPreview != IntPtr.Zero)
                    {
                        var ph = (IPreviewHandler)Marshal.GetObjectForIUnknown(pPreview);
                        Marshal.Release(pPreview);
                        return ph;
                    }
                }
                finally
                {
                    Marshal.Release(pUnk);
                }
            }
            catch { }

            // Fallback: direct cast
            try
            {
                return (IPreviewHandler)handler;
            }
            catch { }

            return null;
        }

        private static object CreateComObject(Guid clsid)
        {
            // Use Activator.CreateInstance which creates a proper .NET RCW
            // that supports interface casting (is/as) reliably.
            try
            {
                Type handlerType = Type.GetTypeFromCLSID(clsid, true);
                if (handlerType != null)
                    return Activator.CreateInstance(handlerType);
            }
            catch { }

            // Fallback: CoCreateInstance with IID_IUnknown
            try
            {
                Guid iid = IID_IUnknown;
                int hr = CoCreateInstance(ref clsid, IntPtr.Zero, CLSCTX_ALL, ref iid, out IntPtr pUnk);
                if (hr == 0 && pUnk != IntPtr.Zero)
                {
                    try
                    {
                        return Marshal.GetObjectForIUnknown(pUnk);
                    }
                    finally
                    {
                        Marshal.Release(pUnk);
                    }
                }
            }
            catch { }

            return null;
        }

        private string _initError; // Detailed init error for diagnostics

        private bool InitializeHandler(object handler, string filePath)
        {
            _initError = null;

            // Try stream initialization first to avoid file locking.
            // Stream-based init gives us control of the file handle (opened with
            // FileShare.ReadWrite), so the file can be opened by other apps.
            if (TryInitializeWithStream(handler, filePath))
                return true;

            // Fallback: file path initialization (some handlers only support this)
            if (TryInitializeWithFile(handler, filePath))
                return true;

            return false;
        }

        private bool TryInitializeWithStream(object handler, string filePath)
        {
            // Strategy 1: C# 'is' cast (works for in-process COM)
            try
            {
                if (handler is IInitializeWithStream initStream)
                {
                    _currentStream = new FileStreamWrapper(filePath);
                    initStream.Initialize(_currentStream, 0);
                    return true;
                }
            }
            catch (Exception ex)
            {
                _initError = $"Stream cast Init: 0x{ex.HResult:X8} {ex.Message}";
                CleanupStream();
            }

            // Strategy 2: explicit COM QueryInterface (works for out-of-process COM)
            try
            {
                var unk = Marshal.GetIUnknownForObject(handler);
                try
                {
                    Guid iid = typeof(IInitializeWithStream).GUID;
                    int hr = Marshal.QueryInterface(unk, ref iid, out IntPtr ptr);
                    if (hr == 0 && ptr != IntPtr.Zero)
                    {
                        try
                        {
                            var initStream = (IInitializeWithStream)Marshal.GetObjectForIUnknown(ptr);
                            _currentStream = new FileStreamWrapper(filePath);
                            initStream.Initialize(_currentStream, 0);
                            return true;
                        }
                        catch (Exception ex)
                        {
                            _initError = $"Stream QI Init: 0x{ex.HResult:X8} {ex.Message}";
                            CleanupStream();
                        }
                        finally
                        {
                            Marshal.Release(ptr);
                        }
                    }
                }
                finally
                {
                    Marshal.Release(unk);
                }
            }
            catch (Exception ex)
            {
                _initError = _initError ?? $"Stream QI: 0x{ex.HResult:X8}";
                CleanupStream();
            }

            return false;
        }

        private bool TryInitializeWithFile(object handler, string filePath)
        {
            // Strategy 1: C# 'is' cast (works for in-process COM)
            try
            {
                if (handler is IInitializeWithFile initFile)
                {
                    initFile.Initialize(filePath, 0);
                    return true;
                }
            }
            catch (Exception ex)
            {
                _initError = $"File cast Init: 0x{ex.HResult:X8} {ex.Message}";
            }

            // Strategy 2: explicit COM QueryInterface (works for out-of-process COM)
            try
            {
                var unk = Marshal.GetIUnknownForObject(handler);
                try
                {
                    Guid iid = typeof(IInitializeWithFile).GUID;
                    int hr = Marshal.QueryInterface(unk, ref iid, out IntPtr ptr);
                    if (hr == 0 && ptr != IntPtr.Zero)
                    {
                        try
                        {
                            var initFile = (IInitializeWithFile)Marshal.GetObjectForIUnknown(ptr);
                            initFile.Initialize(filePath, 0);
                            return true;
                        }
                        catch (Exception ex)
                        {
                            _initError = $"File QI Init: 0x{ex.HResult:X8} {ex.Message}";
                        }
                        finally
                        {
                            Marshal.Release(ptr);
                        }
                    }
                    else
                    {
                        _initError = (_initError ?? "") + $" File QI hr:0x{hr:X8}";
                    }
                }
                finally
                {
                    Marshal.Release(unk);
                }
            }
            catch (Exception ex)
            {
                _initError = (_initError ?? "") + $" File QI outer: 0x{ex.HResult:X8}";
            }

            return false;
        }

        private void CleanupStream()
        {
            if (_currentStream != null)
            {
                try { _currentStream.Dispose(); } catch { }
                _currentStream = null;
            }
        }

        /// <summary>
        /// Temporarily impersonates Medium Integrity Level.
        /// Returns the impersonation token handle (must be closed by caller), or IntPtr.Zero on failure.
        /// Call RevertToSelf() and CloseHandle(token) when done.
        /// </summary>
        private static IntPtr BeginMediumILImpersonation()
        {
            try
            {
                if (!OpenProcessToken(GetCurrentProcess(), TOKEN_DUPLICATE | TOKEN_QUERY, out IntPtr hToken))
                    return IntPtr.Zero;

                try
                {
                    if (!DuplicateTokenEx(hToken,
                        MAXIMUM_ALLOWED,
                        IntPtr.Zero,
                        SecurityImpersonation,
                        TokenImpersonation,
                        out IntPtr hNewToken))
                        return IntPtr.Zero;

                    // Set Medium IL (S-1-16-8192)
                    // SID structure: 1 sub-authority (8192 = 0x2000)
                    // Revision(1) SubAuthorityCount(1) IdentifierAuthority(0,0,0,0,0,16) SubAuthority(8192)
                    byte[] mediumILSid = new byte[]
                    {
                        0x01, // Revision
                        0x01, // SubAuthorityCount
                        0x00, 0x00, 0x00, 0x00, 0x00, 0x10, // SECURITY_MANDATORY_LABEL_AUTHORITY
                        0x00, 0x20, 0x00, 0x00  // SECURITY_MANDATORY_MEDIUM_RID (0x2000)
                    };

                    // TOKEN_MANDATORY_LABEL structure: { SID_AND_ATTRIBUTES { PSID, Attributes } }
                    // Attributes = SE_GROUP_INTEGRITY (0x20)
                    IntPtr pSid = Marshal.AllocHGlobal(mediumILSid.Length);
                    Marshal.Copy(mediumILSid, 0, pSid, mediumILSid.Length);

                    int structSize = IntPtr.Size + sizeof(uint);
                    IntPtr pLabel = Marshal.AllocHGlobal(structSize);
                    Marshal.WriteIntPtr(pLabel, pSid);
                    Marshal.WriteInt32(pLabel, IntPtr.Size, 0x00000020); // SE_GROUP_INTEGRITY

                    bool setOk = SetTokenInformation(hNewToken, TokenIntegrityLevel, pLabel, (uint)structSize);
                    Marshal.FreeHGlobal(pLabel);
                    Marshal.FreeHGlobal(pSid);

                    if (!setOk)
                    {
                        CloseHandle(hNewToken);
                        return IntPtr.Zero;
                    }

                    if (!ImpersonateLoggedOnUser(hNewToken))
                    {
                        CloseHandle(hNewToken);
                        return IntPtr.Zero;
                    }

                    return hNewToken;
                }
                finally
                {
                    CloseHandle(hToken);
                }
            }
            catch
            {
                return IntPtr.Zero;
            }
        }

        private static void EndMediumILImpersonation(IntPtr hToken)
        {
            if (hToken != IntPtr.Zero)
            {
                RevertToSelf();
                CloseHandle(hToken);
            }
        }

        /// <summary>
        /// Reposition and resize the preview host window and handler.
        /// </summary>
        public void Reposition(int left, int top, int width, int height)
        {
            if (_hostHwnd != IntPtr.Zero && width > 0 && height > 0)
            {
                MoveWindow(_hostHwnd, left, top, width, height, true);
            }

            if (_currentHandler != null && width > 0 && height > 0)
            {
                try
                {
                    var rect = new RECT(0, 0, width, height);
                    _currentHandler.SetRect(ref rect);
                }
                catch { }
            }
        }

        #region Registry CLSID lookup

        private const string ShellExPreviewGuid = @"\shellex\{8895b1c6-b41f-4c1c-a562-0d564250836f}";

        private static HashSet<Guid> GetRegisteredHandlers()
        {
            if (_registeredHandlers != null)
                return _registeredHandlers;

            _registeredHandlers = new HashSet<Guid>();
            try
            {
                using (var key = Registry.LocalMachine.OpenSubKey(
                    @"SOFTWARE\Microsoft\Windows\CurrentVersion\PreviewHandlers"))
                {
                    if (key != null)
                    {
                        foreach (string name in key.GetValueNames())
                        {
                            if (Guid.TryParse(name, out Guid g))
                                _registeredHandlers.Add(g);
                        }
                    }
                }
            }
            catch { }
            return _registeredHandlers;
        }

        private static Guid? GetPreviewHandlerCLSID(string extension)
        {
            var registered = GetRegisteredHandlers();
            var candidates = new List<Guid>();

            // 1. Direct: HKCR\<.ext>\shellex\{preview-guid}
            AddCandidate(candidates, extension + ShellExPreviewGuid);

            // 2. Via ProgID
            try
            {
                using (var extKey = Registry.ClassesRoot.OpenSubKey(extension))
                {
                    string progId = extKey?.GetValue(null)?.ToString();
                    if (!string.IsNullOrEmpty(progId))
                    {
                        AddCandidate(candidates, progId + ShellExPreviewGuid);

                        using (var curVerKey = Registry.ClassesRoot.OpenSubKey(progId + @"\CurVer"))
                        {
                            string curVer = curVerKey?.GetValue(null)?.ToString();
                            if (!string.IsNullOrEmpty(curVer))
                                AddCandidate(candidates, curVer + ShellExPreviewGuid);
                        }
                    }
                }
            }
            catch { }

            // 3. SystemFileAssociations by extension
            AddCandidate(candidates, @"SystemFileAssociations\" + extension + ShellExPreviewGuid);

            // 4. SystemFileAssociations by PerceivedType
            try
            {
                using (var extKey = Registry.ClassesRoot.OpenSubKey(extension))
                {
                    string perceivedType = extKey?.GetValue("PerceivedType")?.ToString();
                    if (!string.IsNullOrEmpty(perceivedType))
                        AddCandidate(candidates, @"SystemFileAssociations\" + perceivedType + ShellExPreviewGuid);
                }
            }
            catch { }

            // 5. OpenWithProgids
            try
            {
                using (var extKey = Registry.ClassesRoot.OpenSubKey(extension + @"\OpenWithProgids"))
                {
                    if (extKey != null)
                    {
                        foreach (string valueName in extKey.GetValueNames())
                        {
                            if (!string.IsNullOrEmpty(valueName))
                                AddCandidate(candidates, valueName + ShellExPreviewGuid);
                        }
                    }
                }
            }
            catch { }

            // Return the first candidate that is in the registered handlers list
            foreach (var clsid in candidates)
            {
                if (registered.Contains(clsid))
                    return clsid;
            }

            // Fallback
            if (candidates.Count > 0)
                return candidates[0];

            return null;
        }

        private static void AddCandidate(List<Guid> candidates, string registryPath)
        {
            try
            {
                using (var key = Registry.ClassesRoot.OpenSubKey(registryPath))
                {
                    if (key != null)
                    {
                        string val = key.GetValue(null)?.ToString();
                        if (!string.IsNullOrEmpty(val) && Guid.TryParse(val, out Guid clsid))
                        {
                            if (!candidates.Contains(clsid))
                                candidates.Add(clsid);
                        }
                    }
                }
            }
            catch { }
        }

        #endregion

        public void Dispose()
        {
            if (!_disposed)
            {
                UnloadPreview();
                _disposed = true;
            }
        }

        /// <summary>
        /// Wrapper to provide IStream from a file path.
        /// </summary>
        private class FileStreamWrapper : IStream, IDisposable
        {
            private FileStream _stream;

            public FileStreamWrapper(string filePath)
            {
                _stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            }

            public void Read(byte[] pv, int cb, IntPtr pcbRead)
            {
                if (_stream == null) return;
                int bytesRead = _stream.Read(pv, 0, cb);
                if (pcbRead != IntPtr.Zero)
                    Marshal.WriteInt32(pcbRead, bytesRead);
            }

            public void Write(byte[] pv, int cb, IntPtr pcbWritten)
            {
                throw new NotSupportedException();
            }

            public void Seek(long dlibMove, int dwOrigin, IntPtr plibNewPosition)
            {
                if (_stream == null) return;
                long pos = _stream.Seek(dlibMove, (SeekOrigin)dwOrigin);
                if (plibNewPosition != IntPtr.Zero)
                    Marshal.WriteInt64(plibNewPosition, pos);
            }

            public void SetSize(long libNewSize)
            {
                throw new NotSupportedException();
            }

            public void CopyTo(IStream pstm, long cb, IntPtr pcbRead, IntPtr pcbWritten)
            {
                throw new NotSupportedException();
            }

            public void Commit(int grfCommitFlags) { }
            public void Revert() { }

            public void LockRegion(long libOffset, long cb, int dwLockType)
            {
                throw new NotSupportedException();
            }

            public void UnlockRegion(long libOffset, long cb, int dwLockType)
            {
                throw new NotSupportedException();
            }

            public void Stat(out System.Runtime.InteropServices.ComTypes.STATSTG pstatstg, int grfStatFlag)
            {
                pstatstg = new System.Runtime.InteropServices.ComTypes.STATSTG
                {
                    type = 2, // STGTY_STREAM
                    cbSize = _stream?.Length ?? 0,
                    pwcsName = null,
                };
            }

            public void Clone(out IStream ppstm)
            {
                throw new NotSupportedException();
            }

            public void Dispose()
            {
                if (_stream != null)
                {
                    try { _stream.Dispose(); } catch { }
                    _stream = null;
                }
            }
        }
    }
}
