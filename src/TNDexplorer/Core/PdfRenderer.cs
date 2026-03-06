using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Threading;

namespace TNDexplorer.Core
{
    /// <summary>
    /// Holds an opened PDF document for on-demand page rendering.
    /// Allows querying page count and page sizes without rendering,
    /// and rendering individual pages lazily (for virtualized preview).
    /// </summary>
    internal sealed class PdfDocumentHandle : IDisposable
    {
        private readonly PdfRenderer.IPdfDocumentInternal _pdfDoc;
        private readonly IntPtr _hClassName;
        private bool _disposed;

        public int PageCount { get; }
        public SizeF[] PageSizes { get; }

        /// <summary>
        /// Desired render width in pixels. If 0, uses default (96 DPI page size).
        /// Set before calling RenderPage for higher resolution output.
        /// </summary>
        public uint RenderWidth { get; set; }

        internal PdfDocumentHandle(PdfRenderer.IPdfDocumentInternal pdfDoc, IntPtr hClassName, int pageCount, SizeF[] pageSizes)
        {
            _pdfDoc = pdfDoc;
            _hClassName = hClassName;
            PageCount = pageCount;
            PageSizes = pageSizes;
        }

        /// <summary>
        /// Renders a single page to a System.Drawing.Bitmap.
        /// Thread-safe: can be called from a background thread.
        /// </summary>
        public Bitmap RenderPage(int pageIndex)
        {
            if (_disposed) return null;
            if (pageIndex < 0 || pageIndex >= PageCount) return null;
            return PdfRenderer.RenderSinglePageFromDoc(_pdfDoc, (uint)pageIndex, RenderWidth);
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                _disposed = true;
                try { _pdfDoc?.Dispose(); } catch { }
                if (_hClassName != IntPtr.Zero)
                    PdfRenderer.DeleteHString(_hClassName);
            }
        }
    }

    /// <summary>
    /// Renders PDF pages to bitmaps using the Windows.Data.Pdf WinRT API
    /// via pure COM interop. No WinMD references, no NuGet packages needed.
    /// Works from elevated (admin) processes on Windows 10/11.
    /// </summary>
    internal static class PdfRenderer
    {
        #region P/Invoke declarations

        [DllImport("combase.dll", PreserveSig = false)]
        private static extern void RoGetActivationFactory(
            IntPtr activatableClassId,
            [In] ref Guid iid,
            [MarshalAs(UnmanagedType.IUnknown)] out object factory);

        [DllImport("combase.dll", PreserveSig = false)]
        private static extern void WindowsCreateString(
            [MarshalAs(UnmanagedType.LPWStr)] string sourceString,
            uint length,
            out IntPtr hstring);

        [DllImport("combase.dll", PreserveSig = true)]
        private static extern int WindowsDeleteString(IntPtr hstring);

        [DllImport("shcore.dll", PreserveSig = false)]
        private static extern void CreateRandomAccessStreamOnFile(
            [MarshalAs(UnmanagedType.LPWStr)] string filePath,
            uint accessMode,
            [In] ref Guid riid,
            [MarshalAs(UnmanagedType.IUnknown)] out object stream);

        [DllImport("shcore.dll", PreserveSig = false)]
        private static extern void CreateRandomAccessStreamOverStream(
            IStream stream,
            uint options,
            [In] ref Guid riid,
            [MarshalAs(UnmanagedType.IUnknown)] out object randomAccessStream);

        [DllImport("ole32.dll", PreserveSig = false)]
        private static extern void CreateStreamOnHGlobal(
            IntPtr hGlobal,
            [MarshalAs(UnmanagedType.Bool)] bool fDeleteOnRelease,
            out IStream ppstm);

        [DllImport("combase.dll", PreserveSig = false)]
        private static extern void RoActivateInstance(
            IntPtr activatableClassId,
            [MarshalAs(UnmanagedType.IUnknown)] out object instance);

        #endregion

        #region WinRT COM Interfaces

        [ComImport]
        [Guid("433A0B5F-C007-4788-90F2-08143D922599")]
        [InterfaceType(ComInterfaceType.InterfaceIsIInspectable)]
        private interface IPdfDocumentStatics
        {
            void LoadFromFileAsync(IntPtr file, out IntPtr asyncOp);
            void LoadFromFileWithPasswordAsync(IntPtr file, IntPtr password, out IntPtr asyncOp);
            void LoadFromStreamAsync([MarshalAs(UnmanagedType.IUnknown)] object stream, out IntPtr asyncOp);
            void LoadFromStreamWithPasswordAsync([MarshalAs(UnmanagedType.IUnknown)] object stream, IntPtr password, out IntPtr asyncOp);
        }

        [ComImport]
        [Guid("AC7EBEDD-80FA-4089-846E-81B77FF5A86C")]
        [InterfaceType(ComInterfaceType.InterfaceIsIInspectable)]
        private interface IPdfDocument
        {
            void GetPage(uint pageIndex, out IntPtr page);
            void get_PageCount(out uint pageCount);
            void get_IsPasswordProtected([MarshalAs(UnmanagedType.Bool)] out bool value);
        }

        /// <summary>
        /// Wraps IPdfDocument for external (PdfDocumentHandle) access without exposing COM interface.
        /// </summary>
        private sealed class PdfDocWrapper : IPdfDocumentInternal, IDisposable
        {
            private IPdfDocument _doc;
            internal PdfDocWrapper(IPdfDocument doc) { _doc = doc; }
            public void GetPage(uint pageIndex, out IntPtr page) => _doc.GetPage(pageIndex, out page);
            public void get_PageCount(out uint pageCount) => _doc.get_PageCount(out pageCount);
            public void Dispose()
            {
                var doc = _doc;
                _doc = null;
                if (doc != null)
                {
                    try { Marshal.ReleaseComObject(doc); } catch { }
                }
            }
        }

        /// <summary>
        /// Internal interface for PdfDocumentHandle to call rendering without exposing COM types.
        /// </summary>
        internal interface IPdfDocumentInternal : IDisposable
        {
            void GetPage(uint pageIndex, out IntPtr page);
            void get_PageCount(out uint pageCount);
        }

        [ComImport]
        [Guid("9DB4B0C8-5320-4CFC-AD76-493FDAD0E594")]
        [InterfaceType(ComInterfaceType.InterfaceIsIInspectable)]
        private interface IPdfPage
        {
            void RenderToStreamAsync([MarshalAs(UnmanagedType.IUnknown)] object outputStream, out IntPtr asyncAction);
            void RenderWithOptionsToStreamAsync([MarshalAs(UnmanagedType.IUnknown)] object outputStream, [MarshalAs(UnmanagedType.IUnknown)] object options, out IntPtr asyncAction);
            void PreparePageAsync(out IntPtr asyncAction);
            void get_Index(out uint index);
            void get_Size(out SizeF size);
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct WinRTRect
        {
            public float X, Y, Width, Height;
        }

        [ComImport]
        [Guid("3C98056F-B7CF-4C29-9A04-52D90267F425")]
        [InterfaceType(ComInterfaceType.InterfaceIsIInspectable)]
        private interface IPdfPageRenderOptions
        {
            // vtable slot 6-7: SourceRect (get/put)
            void get_SourceRect(out WinRTRect rect);
            void put_SourceRect(WinRTRect rect);
            // vtable slot 8-9: DestinationWidth
            void get_DestinationWidth(out uint value);
            void put_DestinationWidth(uint value);
            // vtable slot 10-11: DestinationHeight
            void get_DestinationHeight(out uint value);
            void put_DestinationHeight(uint value);
        }

        [ComImport]
        [Guid("00000036-0000-0000-C000-000000000046")]
        [InterfaceType(ComInterfaceType.InterfaceIsIInspectable)]
        private interface IAsyncInfo
        {
            void get_Id(out uint id);
            void get_Status(out AsyncStatus status);
            void get_ErrorCode(out int errorCode);
            void Cancel();
            void Close();
        }

        [ComImport]
        [Guid("30D5A829-7FA4-4026-83BB-D75BAE4EA99E")]
        [InterfaceType(ComInterfaceType.InterfaceIsIInspectable)]
        private interface IClosable
        {
            void Close();
        }

        private enum AsyncStatus : int
        {
            Started = 0,
            Completed = 1,
            Canceled = 2,
            Error = 3
        }

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int GetResultsDelegate(IntPtr thisPtr, out IntPtr result);

        #endregion

        #region GUIDs

        private static readonly Guid IID_IRandomAccessStream = new Guid("905A0FE1-BC53-11DF-8C49-001E4FC686DA");
        private static readonly Guid IID_IPdfDocumentStatics = new Guid("433A0B5F-C007-4788-90F2-08143D922599");

        #endregion

        /// <summary>
        /// Renders the first page of a PDF file to a System.Drawing.Bitmap.
        /// </summary>
        /// <param name="pdfPath">Full path to the PDF file.</param>
        /// <param name="maxWidth">Maximum width of the rendered bitmap. 0 = default (96 DPI).</param>
        /// <param name="maxHeight">Maximum height of the rendered bitmap. 0 = default (96 DPI).</param>
        /// <returns>A Bitmap, or null on failure.</returns>
        public static Bitmap RenderFirstPage(string pdfPath, int maxWidth = 0, int maxHeight = 0)
        {
            return RenderPage(pdfPath, 0, maxWidth, maxHeight);
        }

        /// <summary>
        /// Renders a single page of a PDF file to a System.Drawing.Bitmap.
        /// </summary>
        public static Bitmap RenderPage(string pdfPath, uint pageIndex, int maxWidth = 0, int maxHeight = 0)
        {
            IntPtr hClassName = IntPtr.Zero;
            IPdfDocument pdfDoc = null;
            try
            {
                pdfDoc = OpenPdfDocument(pdfPath, out hClassName);
                if (pdfDoc == null)
                    return null;

                pdfDoc.get_PageCount(out uint pageCount);
                if (pageIndex >= pageCount)
                    return null;

                return RenderSinglePage(pdfDoc, pageIndex);
            }
            catch
            {
                return null;
            }
            finally
            {
                if (pdfDoc != null)
                    try { Marshal.ReleaseComObject(pdfDoc); } catch { }
                if (hClassName != IntPtr.Zero)
                    WindowsDeleteString(hClassName);
            }
        }

        /// <summary>
        /// Renders all pages of a PDF, calling onPageRendered for each page as it completes.
        /// This allows the UI to progressively display pages while rendering continues.
        /// </summary>
        /// <param name="pdfPath">Full path to the PDF file.</param>
        /// <param name="onPageRendered">Callback: (pageIndex, totalPages, bitmap). Bitmap ownership is transferred to caller.</param>
        /// <param name="cancellationToken">Token to cancel rendering of remaining pages.</param>
        public static void RenderAllPages(string pdfPath, Action<int, int, Bitmap> onPageRendered, CancellationToken cancellationToken)
        {
            IntPtr hClassName = IntPtr.Zero;
            IPdfDocument pdfDoc = null;
            try
            {
                pdfDoc = OpenPdfDocument(pdfPath, out hClassName);
                if (pdfDoc == null)
                    return;

                pdfDoc.get_PageCount(out uint pageCount);
                int total = (int)pageCount;

                for (uint i = 0; i < pageCount; i++)
                {
                    if (cancellationToken.IsCancellationRequested)
                        break;

                    var bitmap = RenderSinglePage(pdfDoc, i);
                    if (bitmap != null)
                    {
                        onPageRendered?.Invoke((int)i, total, bitmap);
                    }
                }
            }
            catch
            {
                // Silently fail - partial pages already delivered via callback
            }
            finally
            {
                if (pdfDoc != null)
                    try { Marshal.ReleaseComObject(pdfDoc); } catch { }
                if (hClassName != IntPtr.Zero)
                    WindowsDeleteString(hClassName);
            }
        }

        /// <summary>
        /// Opens a PDF document and returns a handle for on-demand page rendering.
        /// The caller must dispose the handle when done.
        /// </summary>
        public static PdfDocumentHandle OpenDocument(string pdfPath)
        {
            IntPtr hClassName = IntPtr.Zero;
            try
            {
                var pdfDoc = OpenPdfDocument(pdfPath, out hClassName);
                if (pdfDoc == null)
                    return null;

                pdfDoc.get_PageCount(out uint pageCount);

                // Get page sizes for placeholder layout
                var pageSizes = new SizeF[(int)pageCount];
                for (uint i = 0; i < pageCount; i++)
                {
                    pdfDoc.GetPage(i, out IntPtr pagePtr);
                    var page = (IPdfPage)Marshal.GetObjectForIUnknown(pagePtr);
                    Marshal.Release(pagePtr);
                    try
                    {
                        page.get_Size(out SizeF size);
                        pageSizes[i] = size;
                    }
                    finally
                    {
                        try { ((IClosable)page).Close(); } catch { }
                        try { Marshal.ReleaseComObject(page); } catch { }
                    }
                }

                // Transfer ownership of hClassName to the handle
                var wrapper = new PdfDocWrapper(pdfDoc);
                var handle = new PdfDocumentHandle(wrapper, hClassName, (int)pageCount, pageSizes);
                hClassName = IntPtr.Zero; // prevent cleanup in finally
                return handle;
            }
            catch
            {
                return null;
            }
            finally
            {
                if (hClassName != IntPtr.Zero)
                    WindowsDeleteString(hClassName);
            }
        }

        /// <summary>
        /// Renders a single page from an IPdfDocumentInternal (used by PdfDocumentHandle).
        /// If renderWidth > 0, renders at higher resolution using PdfPageRenderOptions.
        /// </summary>
        internal static Bitmap RenderSinglePageFromDoc(IPdfDocumentInternal pdfDoc, uint pageIndex, uint renderWidth = 0)
        {
            pdfDoc.GetPage(pageIndex, out IntPtr pagePtr);
            var page = (IPdfPage)Marshal.GetObjectForIUnknown(pagePtr);
            Marshal.Release(pagePtr);

            IStream comStream = null;
            object outputStream = null;
            object renderOptions = null;
            try
            {
                CreateStreamOnHGlobal(IntPtr.Zero, true, out comStream);
                Guid iidRASOut = IID_IRandomAccessStream;
                CreateRandomAccessStreamOverStream(comStream, 0, ref iidRASOut, out outputStream);

                IntPtr renderAsyncPtr;

                if (renderWidth > 0)
                {
                    // Create PdfPageRenderOptions with DestinationWidth for higher resolution
                    renderOptions = CreateRenderOptions(renderWidth);
                    if (renderOptions != null)
                    {
                        page.RenderWithOptionsToStreamAsync(outputStream, renderOptions, out renderAsyncPtr);
                    }
                    else
                    {
                        page.RenderToStreamAsync(outputStream, out renderAsyncPtr);
                    }
                }
                else
                {
                    page.RenderToStreamAsync(outputStream, out renderAsyncPtr);
                }

                WaitForAsyncAction(renderAsyncPtr);

                comStream.Seek(0, 0, IntPtr.Zero);
                var ms = new MemoryStream();
                byte[] buffer = new byte[8192];
                while (true)
                {
                    IntPtr bytesReadPtr = Marshal.AllocCoTaskMem(sizeof(int));
                    try
                    {
                        comStream.Read(buffer, buffer.Length, bytesReadPtr);
                        int bytesRead = Marshal.ReadInt32(bytesReadPtr);
                        if (bytesRead <= 0) break;
                        ms.Write(buffer, 0, bytesRead);
                    }
                    finally
                    {
                        Marshal.FreeCoTaskMem(bytesReadPtr);
                    }
                }

                if (ms.Length == 0)
                {
                    ms.Dispose();
                    return null;
                }
                ms.Position = 0;
                return new Bitmap(ms);
            }
            finally
            {
                try { ((IClosable)page).Close(); } catch { }
                try { Marshal.ReleaseComObject(page); } catch { }
                if (renderOptions != null)
                    try { Marshal.ReleaseComObject(renderOptions); } catch { }
                if (outputStream != null)
                    try { Marshal.ReleaseComObject(outputStream); } catch { }
                if (comStream != null)
                    try { Marshal.ReleaseComObject(comStream); } catch { }
            }
        }

        /// <summary>
        /// Creates a PdfPageRenderOptions COM object with the specified DestinationWidth.
        /// </summary>
        private static object CreateRenderOptions(uint destinationWidth)
        {
            IntPtr hClassName = IntPtr.Zero;
            try
            {
                string className = "Windows.Data.Pdf.PdfPageRenderOptions";
                WindowsCreateString(className, (uint)className.Length, out hClassName);
                RoActivateInstance(hClassName, out object instance);

                var options = (IPdfPageRenderOptions)instance;
                options.put_DestinationWidth(destinationWidth);
                // DestinationHeight is automatically calculated to maintain aspect ratio
                // when only DestinationWidth is set
                return instance;
            }
            catch
            {
                return null;
            }
            finally
            {
                if (hClassName != IntPtr.Zero)
                    WindowsDeleteString(hClassName);
            }
        }

        /// <summary>
        /// Frees an HSTRING. Used by PdfDocumentHandle.Dispose.
        /// </summary>
        internal static void DeleteHString(IntPtr hstring)
        {
            WindowsDeleteString(hstring);
        }

        /// <summary>
        /// Gets the page count of a PDF file.
        /// </summary>
        public static int GetPageCount(string pdfPath)
        {
            IntPtr hClassName = IntPtr.Zero;
            IPdfDocument pdfDoc = null;
            try
            {
                pdfDoc = OpenPdfDocument(pdfPath, out hClassName);
                if (pdfDoc == null)
                    return 0;

                pdfDoc.get_PageCount(out uint pageCount);
                return (int)pageCount;
            }
            catch
            {
                return 0;
            }
            finally
            {
                if (pdfDoc != null)
                    try { Marshal.ReleaseComObject(pdfDoc); } catch { }
                if (hClassName != IntPtr.Zero)
                    WindowsDeleteString(hClassName);
            }
        }

        #region Internal helpers

        /// <summary>
        /// Opens a PDF document via WinRT COM. Caller must free hClassName via WindowsDeleteString.
        /// </summary>
        private static IPdfDocument OpenPdfDocument(string pdfPath, out IntPtr hClassName)
        {
            hClassName = IntPtr.Zero;

            Guid iidRAS = IID_IRandomAccessStream;
            CreateRandomAccessStreamOnFile(pdfPath, 0 /* Read */, ref iidRAS, out object fileStream);

            string className = "Windows.Data.Pdf.PdfDocument";
            WindowsCreateString(className, (uint)className.Length, out hClassName);

            Guid iid = IID_IPdfDocumentStatics;
            RoGetActivationFactory(hClassName, ref iid, out object factoryObj);
            var factory = (IPdfDocumentStatics)factoryObj;

            try
            {
                factory.LoadFromStreamAsync(fileStream, out IntPtr asyncOpPtr);
                object pdfDocObj = WaitForAsyncOperation(asyncOpPtr);
                if (pdfDocObj == null)
                    return null;

                return (IPdfDocument)pdfDocObj;
            }
            finally
            {
                try { Marshal.ReleaseComObject(factoryObj); } catch { }
                // fileStream is kept alive — the PDF document may hold a reference
            }
        }

        /// <summary>
        /// Renders a single page from an already-opened PDF document.
        /// </summary>
        private static Bitmap RenderSinglePage(IPdfDocument pdfDoc, uint pageIndex)
        {
            pdfDoc.GetPage(pageIndex, out IntPtr pagePtr);
            var page = (IPdfPage)Marshal.GetObjectForIUnknown(pagePtr);
            Marshal.Release(pagePtr);

            IStream comStream = null;
            object outputStream = null;
            try
            {
                // Create output stream for rendering
                CreateStreamOnHGlobal(IntPtr.Zero, true, out comStream);
                Guid iidRASOut = IID_IRandomAccessStream;
                CreateRandomAccessStreamOverStream(comStream, 0, ref iidRASOut, out outputStream);

                // Render page to stream (PNG format)
                page.RenderToStreamAsync(outputStream, out IntPtr renderAsyncPtr);
                WaitForAsyncAction(renderAsyncPtr);

                // Read PNG from IStream
                comStream.Seek(0, 0 /* STREAM_SEEK_SET */, IntPtr.Zero);

                var ms = new MemoryStream();
                byte[] buffer = new byte[8192];
                while (true)
                {
                    IntPtr bytesReadPtr = Marshal.AllocCoTaskMem(sizeof(int));
                    try
                    {
                        comStream.Read(buffer, buffer.Length, bytesReadPtr);
                        int bytesRead = Marshal.ReadInt32(bytesReadPtr);
                        if (bytesRead <= 0) break;
                        ms.Write(buffer, 0, bytesRead);
                    }
                    finally
                    {
                        Marshal.FreeCoTaskMem(bytesReadPtr);
                    }
                }

                if (ms.Length == 0)
                {
                    ms.Dispose();
                    return null;
                }

                ms.Position = 0;
                return new Bitmap(ms);
            }
            finally
            {
                try { ((IClosable)page).Close(); } catch { }
                try { Marshal.ReleaseComObject(page); } catch { }
                if (outputStream != null)
                    try { Marshal.ReleaseComObject(outputStream); } catch { }
                if (comStream != null)
                    try { Marshal.ReleaseComObject(comStream); } catch { }
            }
        }

        #endregion

        #region Async helpers

        private static object WaitForAsyncOperation(IntPtr asyncOpPtr)
        {
            Guid iidAsyncInfo = new Guid("00000036-0000-0000-C000-000000000046");
            Marshal.QueryInterface(asyncOpPtr, ref iidAsyncInfo, out IntPtr asyncInfoPtr);
            var asyncInfo = (IAsyncInfo)Marshal.GetObjectForIUnknown(asyncInfoPtr);
            Marshal.Release(asyncInfoPtr);

            // Poll for completion (with timeout)
            int maxWait = 10000; // 10 seconds
            int waited = 0;
            while (waited < maxWait)
            {
                asyncInfo.get_Status(out AsyncStatus status);
                if (status == AsyncStatus.Completed)
                    break;
                if (status == AsyncStatus.Error)
                {
                    asyncInfo.get_ErrorCode(out int errorCode);
                    Marshal.Release(asyncOpPtr);
                    throw new COMException("Async operation failed", errorCode);
                }
                if (status == AsyncStatus.Canceled)
                {
                    Marshal.Release(asyncOpPtr);
                    throw new OperationCanceledException();
                }

                Thread.Sleep(10);
                waited += 10;
            }

            if (waited >= maxWait)
            {
                Marshal.Release(asyncOpPtr);
                return null;
            }

            // GetResults via vtable slot 8
            IntPtr vtable = Marshal.ReadIntPtr(asyncOpPtr);
            IntPtr getResultsPtr = Marshal.ReadIntPtr(vtable, 8 * IntPtr.Size);
            var getResults = (GetResultsDelegate)Marshal.GetDelegateForFunctionPointer(getResultsPtr, typeof(GetResultsDelegate));
            int hr = getResults(asyncOpPtr, out IntPtr resultPtr);
            Marshal.Release(asyncOpPtr);

            if (hr < 0)
                Marshal.ThrowExceptionForHR(hr);

            if (resultPtr == IntPtr.Zero)
                return null;

            object result = Marshal.GetObjectForIUnknown(resultPtr);
            Marshal.Release(resultPtr);
            return result;
        }

        private static void WaitForAsyncAction(IntPtr asyncActionPtr)
        {
            Guid iidAsyncInfo = new Guid("00000036-0000-0000-C000-000000000046");
            Marshal.QueryInterface(asyncActionPtr, ref iidAsyncInfo, out IntPtr asyncInfoPtr);
            var asyncInfo = (IAsyncInfo)Marshal.GetObjectForIUnknown(asyncInfoPtr);
            Marshal.Release(asyncInfoPtr);

            int maxWait = 10000;
            int waited = 0;
            while (waited < maxWait)
            {
                asyncInfo.get_Status(out AsyncStatus status);
                if (status == AsyncStatus.Completed)
                    break;
                if (status == AsyncStatus.Error)
                {
                    asyncInfo.get_ErrorCode(out int errorCode);
                    Marshal.Release(asyncActionPtr);
                    throw new COMException("Async action failed", errorCode);
                }
                if (status == AsyncStatus.Canceled)
                {
                    Marshal.Release(asyncActionPtr);
                    throw new OperationCanceledException();
                }

                Thread.Sleep(10);
                waited += 10;
            }

            Marshal.Release(asyncActionPtr);
        }

        #endregion
    }
}
