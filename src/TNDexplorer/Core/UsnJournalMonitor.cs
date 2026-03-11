using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using TNDexplorer.Native;

namespace TNDexplorer.Core
{
    public class FileChangeEventArgs : EventArgs
    {
        public FileEntry Entry { get; set; }
        public FileChangeType ChangeType { get; set; }
    }

    public enum FileChangeType
    {
        Created,
        Deleted,
        Renamed,
        Modified,
    }

    public class UsnJournalMonitor : IDisposable
    {
        private readonly IntPtr _volumeHandle;
        private readonly byte _driveIndex;
        private readonly FileIndex _index;
        private readonly ulong _journalId;
        private long _lastUsn;
        private CancellationTokenSource _cts;
        private Task _monitorTask;
        private bool _disposed;

        public event EventHandler<FileChangeEventArgs> FileChanged;

        /// <summary>
        /// Raised when the USN Journal has overflowed (entries deleted/wrapped around).
        /// The subscriber should trigger a full MFT rescan for this drive.
        /// </summary>
        public event EventHandler JournalOverflow;

        public UsnJournalMonitor(IntPtr volumeHandle, byte driveIndex, FileIndex index,
            ulong journalId, long startUsn)
        {
            _volumeHandle = volumeHandle;
            _driveIndex = driveIndex;
            _index = index;
            _journalId = journalId;
            _lastUsn = startUsn;
        }

        public void Start()
        {
            if (_monitorTask != null)
                return;

            _cts = new CancellationTokenSource();
            _monitorTask = Task.Factory.StartNew(
                () => MonitorLoop(_cts.Token),
                _cts.Token,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default);
        }

        public void Stop()
        {
            _cts?.Cancel();
            try
            {
                _monitorTask?.Wait(TimeSpan.FromSeconds(5));
            }
            catch (AggregateException) { }
            _monitorTask = null;
        }

        private void MonitorLoop(CancellationToken ct)
        {
            const int BUFFER_SIZE = 64 * 1024;
            IntPtr buffer = Marshal.AllocHGlobal(BUFFER_SIZE);

            try
            {
                int readDataSize = Marshal.SizeOf(typeof(READ_USN_JOURNAL_DATA_V0));
                IntPtr readDataPtr = Marshal.AllocHGlobal(readDataSize);

                try
                {
                    while (!ct.IsCancellationRequested)
                    {
                        var readData = new READ_USN_JOURNAL_DATA_V0
                        {
                            StartUsn = _lastUsn,
                            ReasonMask = NativeConstants.USN_REASON_FILE_CREATE
                                       | NativeConstants.USN_REASON_FILE_DELETE
                                       | NativeConstants.USN_REASON_RENAME_NEW_NAME
                                       | NativeConstants.USN_REASON_BASIC_INFO_CHANGE
                                       | NativeConstants.USN_REASON_DATA_EXTEND
                                       | NativeConstants.USN_REASON_DATA_TRUNCATION
                                       | NativeConstants.USN_REASON_CLOSE,
                            ReturnOnlyOnClose = 0,
                            Timeout = 0,
                            BytesToWaitFor = 1,
                            UsnJournalID = _journalId
                        };

                        Marshal.StructureToPtr(readData, readDataPtr, true);

                        bool success = NativeMethods.DeviceIoControl(
                            _volumeHandle,
                            NativeConstants.FSCTL_READ_USN_JOURNAL,
                            readDataPtr,
                            readDataSize,
                            buffer,
                            BUFFER_SIZE,
                            out int bytesReturned,
                            IntPtr.Zero);

                        if (!success)
                        {
                            int error = Marshal.GetLastWin32Error();
                            if (error == NativeConstants.ERROR_JOURNAL_ENTRY_DELETED
                                || error == NativeConstants.ERROR_JOURNAL_DELETE_IN_PROGRESS
                                || error == NativeConstants.ERROR_JOURNAL_NOT_ACTIVE)
                            {
                                Debug.WriteLine($"USN Journal overflow/reset detected on drive index {_driveIndex} (error={error}). Requesting full rescan.");
                                JournalOverflow?.Invoke(this, EventArgs.Empty);
                            }
                            else
                            {
                                Debug.WriteLine($"FSCTL_READ_USN_JOURNAL failed on drive index {_driveIndex} with error {error}");
                            }
                            break;
                        }

                        if (ct.IsCancellationRequested)
                            break;

                        if (bytesReturned <= 8)
                        {
                            // No records, just the next USN
                            _lastUsn = Marshal.ReadInt64(buffer, 0);
                            continue;
                        }

                        _lastUsn = Marshal.ReadInt64(buffer, 0);

                        int offset = 8;
                        while (offset < bytesReturned)
                        {
                            if (ct.IsCancellationRequested) break;

                            uint recordLength = (uint)Marshal.ReadInt32(buffer, offset);
                            if (recordLength == 0) break;

                            ProcessRecord(buffer, offset);

                            offset += (int)recordLength;
                        }
                    }
                }
                finally
                {
                    Marshal.FreeHGlobal(readDataPtr);
                }
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        private void ProcessRecord(IntPtr buffer, int offset)
        {
            IntPtr recordPtr = IntPtr.Add(buffer, offset);

            ushort majorVersion = (ushort)Marshal.ReadInt16(recordPtr, 4);
            if (majorVersion != 2) return;

            ulong fileReferenceNumber = (ulong)Marshal.ReadInt64(recordPtr, 8) & 0x0000FFFFFFFFFFFF;
            ulong parentFileReferenceNumber = (ulong)Marshal.ReadInt64(recordPtr, 16) & 0x0000FFFFFFFFFFFF;
            uint reason = (uint)Marshal.ReadInt32(recordPtr, 40);
            uint fileAttributes = (uint)Marshal.ReadInt32(recordPtr, 52);
            ushort fileNameLength = (ushort)Marshal.ReadInt16(recordPtr, 56);
            ushort fileNameOffset = (ushort)Marshal.ReadInt16(recordPtr, 58);

            if (fileNameLength == 0) return;

            string fileName = Marshal.PtrToStringUni(
                IntPtr.Add(recordPtr, fileNameOffset),
                fileNameLength / 2);

            if (string.IsNullOrEmpty(fileName)) return;

            var flags = FileEntryFlags.None;
            if ((fileAttributes & NativeConstants.FILE_ATTRIBUTE_DIRECTORY) != 0)
                flags |= FileEntryFlags.Directory;
            if ((fileAttributes & NativeConstants.FILE_ATTRIBUTE_HIDDEN) != 0)
                flags |= FileEntryFlags.Hidden;
            if ((fileAttributes & NativeConstants.FILE_ATTRIBUTE_SYSTEM) != 0)
                flags |= FileEntryFlags.System;

            var entry = new FileEntry
            {
                FileReferenceNumber = fileReferenceNumber,
                ParentFileReferenceNumber = parentFileReferenceNumber,
                Name = fileName,
                Flags = flags,
                DriveIndex = _driveIndex,
            };

            if ((reason & NativeConstants.USN_REASON_FILE_CREATE) != 0)
            {
                _index.AddEntry(_driveIndex, entry);
                FileChanged?.Invoke(this, new FileChangeEventArgs
                {
                    Entry = entry,
                    ChangeType = FileChangeType.Created
                });
            }
            else if ((reason & NativeConstants.USN_REASON_FILE_DELETE) != 0)
            {
                _index.RemoveEntry(_driveIndex, fileReferenceNumber);
                FileChanged?.Invoke(this, new FileChangeEventArgs
                {
                    Entry = entry,
                    ChangeType = FileChangeType.Deleted
                });
            }
            else if ((reason & NativeConstants.USN_REASON_RENAME_NEW_NAME) != 0)
            {
                _index.UpdateEntry(_driveIndex, fileReferenceNumber, entry);
                FileChanged?.Invoke(this, new FileChangeEventArgs
                {
                    Entry = entry,
                    ChangeType = FileChangeType.Renamed
                });
            }
            else if ((reason & (NativeConstants.USN_REASON_BASIC_INFO_CHANGE
                              | NativeConstants.USN_REASON_DATA_EXTEND
                              | NativeConstants.USN_REASON_DATA_TRUNCATION)) != 0)
            {
                _index.UpdateEntry(_driveIndex, fileReferenceNumber, entry);
                FileChanged?.Invoke(this, new FileChangeEventArgs
                {
                    Entry = entry,
                    ChangeType = FileChangeType.Modified
                });
            }
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                Stop();
                _cts?.Dispose();
                _disposed = true;
            }
        }
    }
}
