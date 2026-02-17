namespace TNDexplorer.Native
{
    internal static class NativeConstants
    {
        // FSCTL codes
        internal const uint FSCTL_ENUM_USN_DATA = 0x000900B3;
        internal const uint FSCTL_READ_USN_JOURNAL = 0x000900BB;
        internal const uint FSCTL_QUERY_USN_JOURNAL = 0x000900F4;
        internal const uint FSCTL_CREATE_USN_JOURNAL = 0x000900E7;

        // CreateFile access flags
        internal const uint GENERIC_READ = 0x80000000;
        internal const uint GENERIC_WRITE = 0x40000000;

        // CreateFile share mode
        internal const uint FILE_SHARE_READ = 0x00000001;
        internal const uint FILE_SHARE_WRITE = 0x00000002;
        internal const uint FILE_SHARE_DELETE = 0x00000004;

        // CreateFile creation disposition
        internal const uint OPEN_EXISTING = 3;

        // CreateFile flags
        internal const uint FILE_FLAG_BACKUP_SEMANTICS = 0x02000000;

        // File attributes
        internal const uint FILE_ATTRIBUTE_DIRECTORY = 0x00000010;
        internal const uint FILE_ATTRIBUTE_HIDDEN = 0x00000002;
        internal const uint FILE_ATTRIBUTE_SYSTEM = 0x00000004;

        // USN reason flags
        internal const uint USN_REASON_DATA_OVERWRITE = 0x00000001;
        internal const uint USN_REASON_DATA_EXTEND = 0x00000002;
        internal const uint USN_REASON_DATA_TRUNCATION = 0x00000004;
        internal const uint USN_REASON_NAMED_DATA_OVERWRITE = 0x00000010;
        internal const uint USN_REASON_NAMED_DATA_EXTEND = 0x00000020;
        internal const uint USN_REASON_NAMED_DATA_TRUNCATION = 0x00000040;
        internal const uint USN_REASON_FILE_CREATE = 0x00000100;
        internal const uint USN_REASON_FILE_DELETE = 0x00000200;
        internal const uint USN_REASON_EA_CHANGE = 0x00000400;
        internal const uint USN_REASON_SECURITY_CHANGE = 0x00000800;
        internal const uint USN_REASON_RENAME_OLD_NAME = 0x00001000;
        internal const uint USN_REASON_RENAME_NEW_NAME = 0x00002000;
        internal const uint USN_REASON_INDEXABLE_CHANGE = 0x00004000;
        internal const uint USN_REASON_BASIC_INFO_CHANGE = 0x00008000;
        internal const uint USN_REASON_HARD_LINK_CHANGE = 0x00010000;
        internal const uint USN_REASON_COMPRESSION_CHANGE = 0x00020000;
        internal const uint USN_REASON_ENCRYPTION_CHANGE = 0x00040000;
        internal const uint USN_REASON_OBJECT_ID_CHANGE = 0x00080000;
        internal const uint USN_REASON_REPARSE_POINT_CHANGE = 0x00100000;
        internal const uint USN_REASON_STREAM_CHANGE = 0x00200000;
        internal const uint USN_REASON_CLOSE = 0x80000000;

        // Error codes
        internal const int ERROR_HANDLE_EOF = 38;
        internal const int ERROR_JOURNAL_NOT_ACTIVE = 1179;

        // Invalid handle
        internal static readonly System.IntPtr INVALID_HANDLE_VALUE = new System.IntPtr(-1);

        // SHGetFileInfo flags
        internal const uint SHGFI_ICON = 0x000000100;
        internal const uint SHGFI_SMALLICON = 0x000000001;
        internal const uint SHGFI_USEFILEATTRIBUTES = 0x000000010;
    }
}
