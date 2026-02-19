using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

namespace TNDexplorer.Core
{
    /// <summary>
    /// Event args for full-text crawl progress updates.
    /// </summary>
    public class FullTextCrawlProgressEventArgs : EventArgs
    {
        public int ProcessedFiles { get; set; }
        public int TotalTargetFiles { get; set; }
        public int SkippedFiles { get; set; }
        public bool IsComplete { get; set; }
        public string CurrentFile { get; set; }
    }

    /// <summary>
    /// Background crawler that extracts text from files and feeds
    /// the FullTextIndex. Runs on a dedicated STA thread (required for IFilter COM).
    /// </summary>
    public class FullTextCrawler : IDisposable
    {
        private readonly FileIndex _fileIndex;
        private readonly FullTextIndex _ftsIndex;
        private readonly TextExtractor _extractor;
        private readonly HashSet<string> _targetExtensions;
        private readonly long _maxFileSizeMB;

        private Thread _crawlThread;
        private CancellationTokenSource _cts;
        private bool _disposed;
        private readonly object _pendingLock = new object();
        private readonly Queue<string> _pendingChanges = new Queue<string>();

        // Batch size for committing to SQLite
        private const int BATCH_SIZE = 50;

        public event EventHandler<FullTextCrawlProgressEventArgs> ProgressChanged;

        /// <summary>
        /// Current crawl progress.
        /// </summary>
        public int ProcessedFiles { get; private set; }
        public int TotalTargetFiles { get; private set; }
        public int SkippedFiles { get; private set; }
        public bool IsCrawling { get; private set; }

        /// <summary>
        /// Default target file extensions for full-text indexing.
        /// </summary>
        public static readonly string[] DefaultTargetExtensions = new[]
        {
            ".txt", ".csv", ".tsv", ".log", ".md", ".json", ".xml", ".html", ".htm",
            ".doc", ".xls", ".ppt", ".docx", ".xlsx", ".pptx", ".pdf", ".rtf",
        };

        public FullTextCrawler(
            FileIndex fileIndex,
            FullTextIndex ftsIndex,
            TextExtractor extractor,
            IEnumerable<string> targetExtensions = null,
            long maxFileSizeMB = 50)
        {
            _fileIndex = fileIndex;
            _ftsIndex = ftsIndex;
            _extractor = extractor;
            _maxFileSizeMB = maxFileSizeMB;

            _targetExtensions = new HashSet<string>(
                targetExtensions ?? DefaultTargetExtensions,
                StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Start the background crawl. Enumerates all files in the FileIndex
        /// that match target extensions and indexes their content.
        /// </summary>
        public void StartCrawl(CancellationToken externalCt)
        {
            if (IsCrawling) return;

            _cts = CancellationTokenSource.CreateLinkedTokenSource(externalCt);
            IsCrawling = true;

            // Create STA thread for IFilter COM
            _crawlThread = new Thread(() => CrawlLoop(_cts.Token));
            _crawlThread.SetApartmentState(ApartmentState.STA);
            _crawlThread.IsBackground = true;
            _crawlThread.Name = "FTS-Crawler";
            _crawlThread.Priority = ThreadPriority.BelowNormal;
            _crawlThread.Start();
        }

        /// <summary>
        /// Stop the crawl gracefully.
        /// </summary>
        public void StopCrawl()
        {
            _cts?.Cancel();
            if (_crawlThread != null && _crawlThread.IsAlive)
            {
                _crawlThread.Join(TimeSpan.FromSeconds(10));
            }
            IsCrawling = false;
        }

        /// <summary>
        /// Notify the crawler that a file has changed (from USN Journal).
        /// The crawler will re-index the file in the background.
        /// </summary>
        public void OnFileChanged(string filePath, FileChangeType changeType)
        {
            if (string.IsNullOrEmpty(filePath)) return;

            if (changeType == FileChangeType.Deleted)
            {
                // Remove from FTS index immediately
                try { _ftsIndex.RemoveFile(filePath); }
                catch { }
                return;
            }

            // Check if extension matches
            string ext = Path.GetExtension(filePath);
            if (string.IsNullOrEmpty(ext) || !_targetExtensions.Contains(ext))
                return;

            // Queue for re-indexing
            lock (_pendingLock)
            {
                _pendingChanges.Enqueue(filePath);
            }
        }

        private void CrawlLoop(CancellationToken ct)
        {
            try
            {
                // Phase 1: Initial full crawl
                PerformFullCrawl(ct);

                // Phase 2: Optimize after initial crawl
                if (!ct.IsCancellationRequested)
                {
                    try { _ftsIndex.Optimize(); }
                    catch { }
                }

                // Phase 3: Process pending changes continuously
                while (!ct.IsCancellationRequested)
                {
                    ProcessPendingChanges(ct);
                    Thread.Sleep(2000); // Check every 2 seconds
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"FTS crawler error: {ex.Message}");
            }
            finally
            {
                IsCrawling = false;
            }
        }

        private void PerformFullCrawl(CancellationToken ct)
        {
            // Collect target files from FileIndex
            var targetFiles = CollectTargetFiles();
            TotalTargetFiles = targetFiles.Count;
            ProcessedFiles = 0;
            SkippedFiles = 0;

            RaiseProgress(false, null);

            var batch = new List<(string filePath, string content, long fileSize, DateTime modifiedDate)>();

            foreach (var fileInfo in targetFiles)
            {
                if (ct.IsCancellationRequested) break;

                try
                {
                    string filePath = fileInfo.FullPath;

                    // Check if re-indexing is needed
                    if (!_ftsIndex.NeedsReindex(filePath, fileInfo.Modified))
                    {
                        ProcessedFiles++;
                        SkippedFiles++;
                        continue;
                    }

                    // Extract text content
                    string content = _extractor.ExtractText(filePath);
                    if (!string.IsNullOrEmpty(content))
                    {
                        batch.Add((filePath, content, fileInfo.Size, fileInfo.Modified));
                    }

                    ProcessedFiles++;

                    // Commit batch
                    if (batch.Count >= BATCH_SIZE)
                    {
                        _ftsIndex.UpsertFileBatch(batch);
                        batch.Clear();
                        RaiseProgress(false, filePath);
                    }

                    // Yield CPU to keep UI responsive
                    if (ProcessedFiles % 10 == 0)
                    {
                        Thread.Sleep(1);
                    }
                }
                catch (Exception ex)
                {
                    ProcessedFiles++;
                    SkippedFiles++;
                    System.Diagnostics.Debug.WriteLine($"FTS crawl error for {fileInfo.FullPath}: {ex.Message}");
                }
            }

            // Commit remaining batch
            if (batch.Count > 0)
            {
                try { _ftsIndex.UpsertFileBatch(batch); }
                catch { }
            }

            RaiseProgress(true, null);
        }

        /// <summary>
        /// Collect all files from the FileIndex that match target extensions.
        /// </summary>
        private List<CrawlTargetFile> CollectTargetFiles()
        {
            var targets = new List<CrawlTargetFile>();

            // Enumerate all entries in the FileIndex
            var allEntries = _fileIndex.GetAllEntries();
            if (allEntries == null) return targets;

            foreach (var entry in allEntries)
            {
                if (entry.IsDirectory) continue;
                if (string.IsNullOrEmpty(entry.Name)) continue;

                // Check extension
                int dotIndex = entry.Name.LastIndexOf('.');
                if (dotIndex < 0) continue;

                string ext = entry.Name.Substring(dotIndex);
                if (!_targetExtensions.Contains(ext)) continue;

                // Check file size
                if (entry.Size > _maxFileSizeMB * 1024 * 1024) continue;

                // Resolve full path
                string dirPath = _fileIndex.ResolvePath(entry);
                if (string.IsNullOrEmpty(dirPath)) continue;

                string fullPath = dirPath + "\\" + entry.Name;

                targets.Add(new CrawlTargetFile
                {
                    FullPath = fullPath,
                    Size = entry.Size,
                    Modified = entry.ModifiedDate,
                });
            }

            return targets;
        }

        private void ProcessPendingChanges(CancellationToken ct)
        {
            var pending = new List<string>();
            lock (_pendingLock)
            {
                while (_pendingChanges.Count > 0 && pending.Count < BATCH_SIZE)
                {
                    pending.Add(_pendingChanges.Dequeue());
                }
            }

            if (pending.Count == 0) return;

            var batch = new List<(string filePath, string content, long fileSize, DateTime modifiedDate)>();

            foreach (var filePath in pending)
            {
                if (ct.IsCancellationRequested) break;

                try
                {
                    if (!File.Exists(filePath))
                    {
                        _ftsIndex.RemoveFile(filePath);
                        continue;
                    }

                    var fi = new FileInfo(filePath);
                    string content = _extractor.ExtractText(filePath);
                    if (!string.IsNullOrEmpty(content))
                    {
                        batch.Add((filePath, content, fi.Length, fi.LastWriteTime));
                    }
                }
                catch { }
            }

            if (batch.Count > 0)
            {
                try { _ftsIndex.UpsertFileBatch(batch); }
                catch { }
            }
        }

        private void RaiseProgress(bool isComplete, string currentFile)
        {
            ProgressChanged?.Invoke(this, new FullTextCrawlProgressEventArgs
            {
                ProcessedFiles = ProcessedFiles,
                TotalTargetFiles = TotalTargetFiles,
                SkippedFiles = SkippedFiles,
                IsComplete = isComplete,
                CurrentFile = currentFile,
            });
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                _disposed = true;
                StopCrawl();
                _cts?.Dispose();
            }
        }

        private class CrawlTargetFile
        {
            public string FullPath { get; set; }
            public long Size { get; set; }
            public DateTime Modified { get; set; }
        }
    }
}
