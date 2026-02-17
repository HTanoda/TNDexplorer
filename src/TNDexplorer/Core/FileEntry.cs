using System;

namespace TNDexplorer.Core
{
    [Flags]
    public enum FileEntryFlags : byte
    {
        None = 0,
        Directory = 1,
        Hidden = 2,
        System = 4,
        Network = 8,
    }

    public class FileEntry
    {
        public ulong FileReferenceNumber;
        public ulong ParentFileReferenceNumber;
        public string Name;
        public long Size;
        public DateTime ModifiedDate;
        public FileEntryFlags Flags;
        public byte DriveIndex;

        public bool IsDirectory => (Flags & FileEntryFlags.Directory) != 0;
        public bool IsHidden => (Flags & FileEntryFlags.Hidden) != 0;
        public bool IsSystem => (Flags & FileEntryFlags.System) != 0;
        public bool IsNetwork => (Flags & FileEntryFlags.Network) != 0;
    }
}
