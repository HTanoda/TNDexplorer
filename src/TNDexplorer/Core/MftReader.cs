using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Threading;
using TNDexplorer.Native;

namespace TNDexplorer.Core
{
    public class MftScanProgress
    {
        public int EntriesFound { get; set; }
        public string DriveLetter { get; set; }
    }

    public class MftReader : IDisposable
    {
        private IntPtr _volumeHandle = NativeConstants.INVALID_HANDLE_VALUE;
        private readonly string _driveLetter;
        private readonly byte _driveIndex;
        private bool _disposed;

        public MftReader(string driveLetter, byte driveIndex)
        {
            _driveLetter = driveLetter.TrimEnd('\\');
            _driveIndex = driveIndex;
        }

        public List<FileEntry> EnumerateAllEntries(
            CancellationToken ct,
            IProgress<MftScanProgress> progress = null)
        {
            var entries = new List<FileEntry>();
            OpenVolume();

            USN_JOURNAL_DATA journalData = QueryOrCreateJournal();

            const int BUFFER_SIZE = 128 * 1024; // 128 KB buffer
            IntPtr buffer = Marshal.AllocHGlobal(BUFFER_SIZE);

            try
            {
                var enumData = new MFT_ENUM_DATA_V0
                {
                    StartFileReferenceNumber = 0,
                    LowUsn = 0,
                    HighUsn = journalData.NextUsn
                };

                int enumDataSize = Marshal.SizeOf(typeof(MFT_ENUM_DATA_V0));
                IntPtr enumDataPtr = Marshal.AllocHGlobal(enumDataSize);

                try
                {
                    int progressCounter = 0;

                    while (!ct.IsCancellationRequested)
                    {
                        Marshal.StructureToPtr(enumData, enumDataPtr, true);

                        bool success = NativeMethods.DeviceIoControl(
                            _volumeHandle,
                            NativeConstants.FSCTL_ENUM_USN_DATA,
                            enumDataPtr,
                            enumDataSize,
                            buffer,
                            BUFFER_SIZE,
                            out int bytesReturned,
                            IntPtr.Zero);

                        if (!success)
                        {
                            int error = Marshal.GetLastWin32Error();
                            if (error == NativeConstants.ERROR_HANDLE_EOF)
                                break;
                            throw new Win32Exception(error,
                                $"FSCTL_ENUM_USN_DATA failed on {_driveLetter} with error {error}");
                        }

                        if (bytesReturned <= 8)
                            break;

                        // First 8 bytes: next StartFileReferenceNumber
                        enumData.StartFileReferenceNumber = (ulong)Marshal.ReadInt64(buffer, 0);

                        // Parse USN records starting at offset 8
                        int offset = 8;
                        while (offset < bytesReturned)
                        {
                            if (ct.IsCancellationRequested)
                                break;

                            // Read record length
                            uint recordLength = (uint)Marshal.ReadInt32(buffer, offset);
                            if (recordLength == 0)
                                break;

                            var entry = ParseUsnRecord(buffer, offset);
                            if (entry != null)
                            {
                                entries.Add(entry);
                                progressCounter++;

                                if (progressCounter % 50000 == 0)
                                {
                                    progress?.Report(new MftScanProgress
                                    {
                                        EntriesFound = entries.Count,
                                        DriveLetter = _driveLetter
                                    });
                                }
                            }

                            offset += (int)recordLength;
                        }
                    }
                }
                finally
                {
                    Marshal.FreeHGlobal(enumDataPtr);
                }
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }

            progress?.Report(new MftScanProgress
            {
                EntriesFound = entries.Count,
                DriveLetter = _driveLetter
            });

            return entries;
        }

        internal USN_JOURNAL_DATA QueryOrCreateJournal()
        {
            if (_volumeHandle == NativeConstants.INVALID_HANDLE_VALUE)
                OpenVolume();

            int journalDataSize = Marshal.SizeOf(typeof(USN_JOURNAL_DATA));
            IntPtr journalDataPtr = Marshal.AllocHGlobal(journalDataSize);

            try
            {
                bool success = NativeMethods.DeviceIoControl(
                    _volumeHandle,
                    NativeConstants.FSCTL_QUERY_USN_JOURNAL,
                    IntPtr.Zero,
                    0,
                    journalDataPtr,
                    journalDataSize,
                    out int bytesReturned,
                    IntPtr.Zero);

                if (!success)
                {
                    int error = Marshal.GetLastWin32Error();
                    if (error == NativeConstants.ERROR_JOURNAL_NOT_ACTIVE)
                    {
                        CreateJournal();
                        // Retry query
                        success = NativeMethods.DeviceIoControl(
                            _volumeHandle,
                            NativeConstants.FSCTL_QUERY_USN_JOURNAL,
                            IntPtr.Zero,
                            0,
                            journalDataPtr,
                            journalDataSize,
                            out bytesReturned,
                            IntPtr.Zero);

                        if (!success)
                            throw new Win32Exception(Marshal.GetLastWin32Error(),
                                "Failed to query USN Journal after creation");
                    }
                    else
                    {
                        throw new Win32Exception(error, "Failed to query USN Journal");
                    }
                }

                return (USN_JOURNAL_DATA)Marshal.PtrToStructure(journalDataPtr, typeof(USN_JOURNAL_DATA));
            }
            finally
            {
                Marshal.FreeHGlobal(journalDataPtr);
            }
        }

        public long GetCurrentUsn()
        {
            var journalData = QueryOrCreateJournal();
            return journalData.NextUsn;
        }

        public IntPtr VolumeHandle
        {
            get
            {
                if (_volumeHandle == NativeConstants.INVALID_HANDLE_VALUE)
                    OpenVolume();
                return _volumeHandle;
            }
        }

        public byte DriveIndex => _driveIndex;
        public string DriveLetter => _driveLetter;

        private void OpenVolume()
        {
            if (_volumeHandle != NativeConstants.INVALID_HANDLE_VALUE)
                return;

            string volumePath = @"\\.\" + _driveLetter;

            _volumeHandle = NativeMethods.CreateFile(
                volumePath,
                NativeConstants.GENERIC_READ,
                NativeConstants.FILE_SHARE_READ | NativeConstants.FILE_SHARE_WRITE | NativeConstants.FILE_SHARE_DELETE,
                IntPtr.Zero,
                NativeConstants.OPEN_EXISTING,
                NativeConstants.FILE_FLAG_BACKUP_SEMANTICS,
                IntPtr.Zero);

            if (_volumeHandle == NativeConstants.INVALID_HANDLE_VALUE)
            {
                int error = Marshal.GetLastWin32Error();
                throw new Win32Exception(error,
                    $"Failed to open volume {volumePath}. Ensure the application is running as Administrator.");
            }
        }

        private void CreateJournal()
        {
            int dataSize = Marshal.SizeOf(typeof(CREATE_USN_JOURNAL_DATA));
            IntPtr dataPtr = Marshal.AllocHGlobal(dataSize);

            try
            {
                var createData = new CREATE_USN_JOURNAL_DATA
                {
                    MaximumSize = 0x800000,      // 8 MB
                    AllocationDelta = 0x100000    // 1 MB
                };
                Marshal.StructureToPtr(createData, dataPtr, false);

                bool success = NativeMethods.DeviceIoControl(
                    _volumeHandle,
                    NativeConstants.FSCTL_CREATE_USN_JOURNAL,
                    dataPtr,
                    dataSize,
                    IntPtr.Zero,
                    0,
                    out int bytesReturned,
                    IntPtr.Zero);

                if (!success)
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "Failed to create USN Journal");
            }
            finally
            {
                Marshal.FreeHGlobal(dataPtr);
            }
        }

        private FileEntry ParseUsnRecord(IntPtr buffer, int offset)
        {
            IntPtr recordPtr = IntPtr.Add(buffer, offset);

            uint recordLength = (uint)Marshal.ReadInt32(recordPtr, 0);
            if (recordLength < 60) // minimum valid USN_RECORD_V2 size
                return null;

            ushort majorVersion = (ushort)Marshal.ReadInt16(recordPtr, 4);
            if (majorVersion != 2)
                return null;

            ulong fileReferenceNumber = (ulong)Marshal.ReadInt64(recordPtr, 8);
            ulong parentFileReferenceNumber = (ulong)Marshal.ReadInt64(recordPtr, 16);
            uint fileAttributes = (uint)Marshal.ReadInt32(recordPtr, 52);
            ushort fileNameLength = (ushort)Marshal.ReadInt16(recordPtr, 56);
            ushort fileNameOffset = (ushort)Marshal.ReadInt16(recordPtr, 58);

            if (fileNameLength == 0)
                return null;

            string fileName = Marshal.PtrToStringUni(
                IntPtr.Add(recordPtr, fileNameOffset),
                fileNameLength / 2);

            if (string.IsNullOrEmpty(fileName))
                return null;

            var flags = FileEntryFlags.None;
            if ((fileAttributes & NativeConstants.FILE_ATTRIBUTE_DIRECTORY) != 0)
                flags |= FileEntryFlags.Directory;
            if ((fileAttributes & NativeConstants.FILE_ATTRIBUTE_HIDDEN) != 0)
                flags |= FileEntryFlags.Hidden;
            if ((fileAttributes & NativeConstants.FILE_ATTRIBUTE_SYSTEM) != 0)
                flags |= FileEntryFlags.System;

            return new FileEntry
            {
                FileReferenceNumber = fileReferenceNumber & 0x0000FFFFFFFFFFFF, // Lower 48 bits
                ParentFileReferenceNumber = parentFileReferenceNumber & 0x0000FFFFFFFFFFFF,
                Name = fileName,
                Flags = flags,
                DriveIndex = _driveIndex,
            };
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                if (_volumeHandle != NativeConstants.INVALID_HANDLE_VALUE)
                {
                    NativeMethods.CloseHandle(_volumeHandle);
                    _volumeHandle = NativeConstants.INVALID_HANDLE_VALUE;
                }
                _disposed = true;
            }
        }
    }
}
