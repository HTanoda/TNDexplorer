using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace TNDexplorer.Core
{
    public class NetworkScanProgress
    {
        public int EntriesFound { get; set; }
        public string RootPath { get; set; }
    }

    public class NetworkDriveScanner
    {
        public List<FileEntry> ScanDrive(
            string rootPath,
            byte driveIndex,
            IReadOnlyList<string> excludePatterns,
            CancellationToken ct,
            IProgress<NetworkScanProgress> progress = null)
        {
            var entries = new List<FileEntry>();
            ulong frnCounter = 0; // Generate pseudo-FRNs for network entries

            ScanDirectory(rootPath, driveIndex, excludePatterns, entries, ref frnCounter, 0, ct, progress);

            progress?.Report(new NetworkScanProgress
            {
                EntriesFound = entries.Count,
                RootPath = rootPath
            });

            return entries;
        }

        private void ScanDirectory(
            string path,
            byte driveIndex,
            IReadOnlyList<string> excludePatterns,
            List<FileEntry> entries,
            ref ulong frnCounter,
            ulong parentFrn,
            CancellationToken ct,
            IProgress<NetworkScanProgress> progress)
        {
            if (ct.IsCancellationRequested) return;

            try
            {
                foreach (var dir in Directory.EnumerateDirectories(path))
                {
                    if (ct.IsCancellationRequested) return;

                    string dirName = Path.GetFileName(dir);
                    if (ShouldExclude(dirName, excludePatterns)) continue;

                    frnCounter++;
                    ulong dirFrn = frnCounter;

                    var dirEntry = new FileEntry
                    {
                        FileReferenceNumber = dirFrn,
                        ParentFileReferenceNumber = parentFrn,
                        Name = dirName,
                        Flags = FileEntryFlags.Directory | FileEntryFlags.Network,
                        DriveIndex = driveIndex,
                    };

                    try
                    {
                        var dirInfo = new DirectoryInfo(dir);
                        dirEntry.ModifiedDate = dirInfo.LastWriteTime;
                    }
                    catch { }

                    entries.Add(dirEntry);

                    if (entries.Count % 10000 == 0)
                    {
                        progress?.Report(new NetworkScanProgress
                        {
                            EntriesFound = entries.Count,
                            RootPath = path
                        });
                    }

                    ScanDirectory(dir, driveIndex, excludePatterns, entries, ref frnCounter, dirFrn, ct, progress);
                }

                foreach (var file in Directory.EnumerateFiles(path))
                {
                    if (ct.IsCancellationRequested) return;

                    string fileName = Path.GetFileName(file);

                    frnCounter++;

                    var fileEntry = new FileEntry
                    {
                        FileReferenceNumber = frnCounter,
                        ParentFileReferenceNumber = parentFrn,
                        Name = fileName,
                        Flags = FileEntryFlags.Network,
                        DriveIndex = driveIndex,
                    };

                    try
                    {
                        var fileInfo = new FileInfo(file);
                        fileEntry.Size = fileInfo.Length;
                        fileEntry.ModifiedDate = fileInfo.LastWriteTime;
                    }
                    catch { }

                    entries.Add(fileEntry);

                    if (entries.Count % 10000 == 0)
                    {
                        progress?.Report(new NetworkScanProgress
                        {
                            EntriesFound = entries.Count,
                            RootPath = path
                        });
                    }
                }
            }
            catch (UnauthorizedAccessException) { }
            catch (DirectoryNotFoundException) { }
            catch (IOException) { }
        }

        private static bool ShouldExclude(string name, IReadOnlyList<string> patterns)
        {
            if (patterns == null) return false;
            for (int i = 0; i < patterns.Count; i++)
            {
                if (string.Equals(name, patterns[i], StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        public async Task<List<FileEntry>> ScanDriveAsync(
            string rootPath,
            byte driveIndex,
            IReadOnlyList<string> excludePatterns,
            CancellationToken ct,
            IProgress<NetworkScanProgress> progress = null)
        {
            return await Task.Run(() => ScanDrive(rootPath, driveIndex, excludePatterns, ct, progress), ct)
                .ConfigureAwait(false);
        }
    }
}
