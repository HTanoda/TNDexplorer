using System;
using System.Runtime.InteropServices;

namespace TNDexplorer.Native
{
    // IFilter COM interface for text extraction from documents
    // Windows standard — query.dll provides LoadIFilter

    #region Enumerations

    [Flags]
    internal enum IFILTER_INIT : uint
    {
        NONE = 0,
        CANON_PARAGRAPHS = 1,
        HARD_RETURNS = 2,
        CANON_HYPHENS = 4,
        CANON_SPACES = 8,
        APPLY_INDEX_ATTRIBUTES = 16,
        APPLY_CRAWL_ATTRIBUTES = 256,
        APPLY_OTHER_ATTRIBUTES = 32,
        INDEXING_ONLY = 64,
        SEARCH_LINKS = 128,
        FILTER_OWNED_VALUE_OK = 512,
    }

    internal enum CHUNK_BREAKTYPE
    {
        CHUNK_NO_BREAK = 0,
        CHUNK_EOW = 1,
        CHUNK_EOS = 2,
        CHUNK_EOP = 3,
        CHUNK_EOC = 4,
    }

    [Flags]
    internal enum CHUNKSTATE
    {
        CHUNK_TEXT = 0x1,
        CHUNK_VALUE = 0x2,
        CHUNK_FILTER_OWNED_VALUE = 0x4,
    }

    #endregion

    #region Structures

    [StructLayout(LayoutKind.Sequential)]
    internal struct PROPSPEC
    {
        public uint ulKind;     // 0 = PRSPEC_LPWSTR, 1 = PRSPEC_PROPID
        public IntPtr propData; // union: PROPID or LPWSTR
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct FULLPROPSPEC
    {
        public Guid guidPropSet;
        public PROPSPEC psProperty;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct STAT_CHUNK
    {
        public uint idChunk;
        public CHUNK_BREAKTYPE breakType;
        public CHUNKSTATE flags;
        public uint locale;
        public FULLPROPSPEC attribute;
        public uint idChunkSource;
        public uint cwcStartSource;
        public uint cwcLenSource;
    }

    #endregion

    #region IFilter COM Interface

    [ComImport]
    [Guid("89BCB740-6119-101A-BCB7-00DD010655AF")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IFilter
    {
        [PreserveSig]
        int Init(
            IFILTER_INIT grfFlags,
            uint cAttributes,
            IntPtr aAttributes,     // FULLPROPSPEC[]
            out uint pdwFlags);

        [PreserveSig]
        int GetChunk(out STAT_CHUNK pStat);

        [PreserveSig]
        int GetText(ref uint pcwcBuffer, [MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder buffer);

        [PreserveSig]
        int GetValue(out IntPtr ppPropValue);

        [PreserveSig]
        int BindRegion(FILTERREGION origPos, ref Guid riid, out object ppunk);
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct FILTERREGION
    {
        public uint idChunk;
        public uint cwcStart;
        public uint cwcExtent;
    }

    #endregion

    #region P/Invoke

    internal static class IFilterNativeMethods
    {
        // LoadIFilter from query.dll — Windows standard component
        [DllImport("query.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        internal static extern int LoadIFilter(
            string pwcsPath,
            IntPtr pUnkOuter,
            out IFilter ppIUnk);

        // HRESULT constants for IFilter
        internal const int S_OK = 0;
        internal const int E_FAIL = unchecked((int)0x80004005);
        internal const int FILTER_E_END_OF_CHUNKS = unchecked((int)0x80041700);
        internal const int FILTER_E_NO_MORE_TEXT = unchecked((int)0x80041701);
        internal const int FILTER_E_NO_MORE_VALUES = unchecked((int)0x80041702);
        internal const int FILTER_E_NO_TEXT = unchecked((int)0x80041705);
        internal const int FILTER_E_ACCESS = unchecked((int)0x80041703);
        internal const int FILTER_E_PASSWORD = unchecked((int)0x8004170B);
        internal const int FILTER_E_UNKNOWNFORMAT = unchecked((int)0x8004170C);

        /// <summary>
        /// Check if query.dll is available on this system.
        /// Returns false if Windows Search is disabled or DLL is missing.
        /// </summary>
        internal static bool IsIFilterAvailable()
        {
            try
            {
                IntPtr hModule = LoadLibrary("query.dll");
                if (hModule == IntPtr.Zero)
                    return false;
                FreeLibrary(hModule);
                return true;
            }
            catch
            {
                return false;
            }
        }

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr LoadLibrary(string lpFileName);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool FreeLibrary(IntPtr hModule);
    }

    #endregion
}
