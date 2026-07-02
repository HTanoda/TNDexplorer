using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TNDexplorer.Models;

namespace TNDexplorer.Core
{
    public class AdvancedSearchOptions
    {
        public bool CaseSensitive { get; set; }
        public DateTime? DateFrom { get; set; }
        public DateTime? DateTo { get; set; }
        public long? MinSize { get; set; }
        public long? MaxSize { get; set; }
        public string FolderPath { get; set; }
        public string ExtensionFilter { get; set; }
        public bool IncludeSubfolders { get; set; } = true;
        public bool ExcludeHiddenFiles { get; set; }
        public bool ExcludeSystemFiles { get; set; }
    }

    /// <summary>
    /// Progress info for real-time content search.
    /// </summary>
    public class ContentSearchProgress
    {
        public int ScannedFiles { get; set; }
        public int TotalFiles { get; set; }
        public int MatchedFiles { get; set; }
        public int FailedFiles { get; set; }
        public bool IsCompleted { get; set; }
    }

    public class SearchEngine
    {
        private readonly FileIndex _index;
        private CancellationTokenSource _currentSearchCts;
        private readonly object _searchLock = new object();

        public SearchEngine(FileIndex index)
        {
            _index = index;
        }

        public async Task<List<FileResultItem>> SearchAsync(
            string query,
            SearchFilter filter,
            int maxResults,
            CancellationToken externalCt,
            AdvancedSearchOptions advancedOptions = null)
        {
            CancellationTokenSource newCts;

            lock (_searchLock)
            {
                _currentSearchCts?.Cancel();
                newCts = CancellationTokenSource.CreateLinkedTokenSource(externalCt);
                _currentSearchCts = newCts;
            }

            try
            {
                return await Task.Run(() =>
                {
                    var entries = _index.Search(query, filter, maxResults, newCts.Token,
                        advancedOptions?.CaseSensitive ?? false,
                        advancedOptions?.ExtensionFilter,
                        advancedOptions?.FolderPath,
                        advancedOptions?.IncludeSubfolders ?? true,
                        advancedOptions?.ExcludeHiddenFiles ?? false,
                        advancedOptions?.ExcludeSystemFiles ?? false);

                    var results = new List<FileResultItem>(Math.Min(entries.Count, maxResults));
                    foreach (var entry in entries)
                    {
                        if (newCts.Token.IsCancellationRequested)
                            break;

                        string dirPath = _index.ResolvePath(entry);
                        string fullPath = dirPath + "\\" + entry.Name;

                        // Retrieve file size and modified date on demand
                        long size = entry.Size;
                        DateTime modified = entry.ModifiedDate;

                        if (modified == default(DateTime) || (size == 0 && !entry.IsDirectory))
                        {
                            try
                            {
                                if (entry.IsDirectory)
                                {
                                    var di = new DirectoryInfo(fullPath);
                                    if (di.Exists)
                                    {
                                        modified = di.LastWriteTime;
                                        entry.ModifiedDate = modified;
                                    }
                                }
                                else
                                {
                                    var fi = new FileInfo(fullPath);
                                    if (fi.Exists)
                                    {
                                        size = fi.Length;
                                        modified = fi.LastWriteTime;
                                        entry.Size = size;
                                        entry.ModifiedDate = modified;
                                    }
                                }
                            }
                            catch { }
                        }

                        // Apply advanced date/size filters (post-retrieval)
                        if (advancedOptions != null)
                        {
                            if (advancedOptions.DateFrom.HasValue && modified < advancedOptions.DateFrom.Value)
                                continue;
                            if (advancedOptions.DateTo.HasValue && modified > advancedOptions.DateTo.Value.AddDays(1))
                                continue;
                            if (advancedOptions.MinSize.HasValue && size < advancedOptions.MinSize.Value)
                                continue;
                            if (advancedOptions.MaxSize.HasValue && size > advancedOptions.MaxSize.Value)
                                continue;
                        }

                        results.Add(new FileResultItem
                        {
                            Name = entry.Name,
                            Path = dirPath,
                            FullPath = fullPath,
                            Size = entry.IsDirectory ? -1 : size,
                            Modified = modified,
                            IsDirectory = entry.IsDirectory,
                        });
                    }

                    // 並列検索＋ConcurrentBag により結果順序は非決定的。決定的な全順序(FullPath)を
                    // 与えることで、同一データなら毎回同一の並びになり、ライブ更新時に同一更新日時の
                    // 行が入れ替わって見える問題を防ぐ（列ソートの同値も安定する）。
                    results.Sort((a, b) =>
                        string.Compare(a.FullPath, b.FullPath, StringComparison.OrdinalIgnoreCase));

                    return results;
                }, newCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return new List<FileResultItem>();
            }
        }

        /// <summary>
        /// Real-time content search (grep-style).
        /// Enumerates files from the FileIndex, reads content via TextExtractor,
        /// and searches for the query string in parallel.
        /// Requires FolderPath in advancedOptions.
        /// </summary>
        public async Task<List<FileResultItem>> ContentSearchAsync(
            string query,
            SearchFilter filter,
            int maxResults,
            CancellationToken externalCt,
            TextExtractor extractor,
            AdvancedSearchOptions advancedOptions = null,
            IProgress<ContentSearchProgress> progress = null,
            IReadOnlyList<string> contentSearchExtensions = null)
        {
            if (string.IsNullOrWhiteSpace(query) || extractor == null)
                return new List<FileResultItem>();

            // Folder path is required for content search
            if (advancedOptions == null || string.IsNullOrWhiteSpace(advancedOptions.FolderPath))
                return new List<FileResultItem>();

            CancellationTokenSource newCts;
            lock (_searchLock)
            {
                _currentSearchCts?.Cancel();
                newCts = CancellationTokenSource.CreateLinkedTokenSource(externalCt);
                _currentSearchCts = newCts;
            }

            var ct = newCts.Token;

            try
            {
                return await Task.Run(() =>
                {
                    // Step 1: Collect target files from FileIndex
                    var targetFiles = CollectTargetFiles(advancedOptions, contentSearchExtensions, ct);
                    int totalFiles = targetFiles.Count;

                    var results = new ConcurrentBag<FileResultItem>();
                    int scannedCount = 0;
                    int matchedCount = 0;
                    int failedCount = 0;
                    var comparison = advancedOptions.CaseSensitive
                        ? StringComparison.Ordinal
                        : StringComparison.OrdinalIgnoreCase;

                    // Step 2: Parallel search through file contents
                    var options = new ParallelOptions
                    {
                        MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount),
                        CancellationToken = ct,
                    };

                    // Separate plain text files from IFilter files
                    // IFilter COM requires STA thread, so process them separately
                    var plainTextFiles = new List<ContentSearchTarget>();
                    var ifilterFiles = new List<ContentSearchTarget>();

                    foreach (var file in targetFiles)
                    {
                        int dotIdx = file.Name.LastIndexOf('.');
                        string ext = dotIdx >= 0 ? file.Name.Substring(dotIdx) : "";
                        if (TextExtractor.IsPlainTextExtension(ext))
                            plainTextFiles.Add(file);
                        else
                            ifilterFiles.Add(file);
                    }

                    // Process plain text files in parallel (no COM dependency)
                    try
                    {
                        Parallel.ForEach(plainTextFiles, options, (file, loopState) =>
                        {
                            if (ct.IsCancellationRequested || results.Count >= maxResults)
                            {
                                loopState.Break();
                                return;
                            }

                            SearchFileContent(file, extractor, query, comparison,
                                maxResults, results, ref scannedCount, ref matchedCount,
                                ref failedCount, totalFiles, progress);
                        });
                    }
                    catch (OperationCanceledException) { }

                    // Process IFilter files on a dedicated STA thread (COM requirement)
                    if (ifilterFiles.Count > 0 && !ct.IsCancellationRequested && results.Count < maxResults)
                    {
                        var staThread = new Thread(() =>
                        {
                            foreach (var file in ifilterFiles)
                            {
                                if (ct.IsCancellationRequested || results.Count >= maxResults)
                                    break;

                                SearchFileContent(file, extractor, query, comparison,
                                    maxResults, results, ref scannedCount, ref matchedCount,
                                    ref failedCount, totalFiles, progress);
                            }
                        });
                        staThread.SetApartmentState(ApartmentState.STA);
                        staThread.IsBackground = true;
                        staThread.Start();
                        staThread.Join();
                    }

                    // Final progress report (mark as completed)
                    progress?.Report(new ContentSearchProgress
                    {
                        ScannedFiles = scannedCount,
                        TotalFiles = totalFiles,
                        MatchedFiles = matchedCount,
                        FailedFiles = failedCount,
                        IsCompleted = true,
                    });

                    return results.Take(maxResults).ToList();
                }, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return new List<FileResultItem>();
            }
        }

        /// <summary>
        /// Collect target files from the FileIndex based on folder path, extension, and size filters.
        /// </summary>
        private List<ContentSearchTarget> CollectTargetFiles(
            AdvancedSearchOptions opts, IReadOnlyList<string> contentSearchExtensions, CancellationToken ct)
        {
            var targets = new List<ContentSearchTarget>();
            var allEntries = _index.GetAllEntries();
            if (allEntries == null) return targets;

            string folderFilter = opts.FolderPath?.TrimEnd('\\', '/') ?? "";
            string folderFilterAlt = null;
            bool includeSubfolders = opts.IncludeSubfolders;

            // Resolve drive letter -> UNC path (or vice versa) for folder filter matching
            if (!string.IsNullOrEmpty(folderFilter))
            {
                if (folderFilter.Length >= 2 && folderFilter[1] == ':')
                {
                    string driveLetter = folderFilter.Substring(0, 2);
                    string uncBase = NetworkDriveDetector.GetUncPath(driveLetter);
                    if (!string.IsNullOrEmpty(uncBase))
                    {
                        string remainder = folderFilter.Length > 2 ? folderFilter.Substring(2) : "";
                        folderFilterAlt = uncBase.TrimEnd('\\') + remainder;
                    }
                }
                else if (folderFilter.StartsWith("\\\\"))
                {
                    for (char c = 'A'; c <= 'Z'; c++)
                    {
                        string drv = c + ":";
                        string uncBase = NetworkDriveDetector.GetUncPath(drv);
                        if (!string.IsNullOrEmpty(uncBase))
                        {
                            string uncNorm = uncBase.TrimEnd('\\');
                            if (folderFilter.StartsWith(uncNorm, StringComparison.OrdinalIgnoreCase))
                            {
                                string remainder = folderFilter.Substring(uncNorm.Length);
                                folderFilterAlt = drv + remainder;
                                break;
                            }
                        }
                    }
                }
            }

            // Parse extension filter from advanced options (user's per-search filter)
            HashSet<string> extFilter = null;
            if (!string.IsNullOrWhiteSpace(opts.ExtensionFilter))
            {
                extFilter = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var ef in opts.ExtensionFilter.Split(new[] { ';', ' ' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    string ext = ef.TrimStart('*').Trim();
                    if (string.IsNullOrEmpty(ext)) continue;
                    if (!ext.StartsWith(".")) ext = "." + ext;
                    extFilter.Add(ext);
                }
            }

            // Build allowed extensions set from settings (content search target extensions)
            HashSet<string> allowedExtensions = null;
            if (extFilter == null && contentSearchExtensions != null && contentSearchExtensions.Count > 0)
            {
                allowedExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var ext in contentSearchExtensions)
                {
                    string e = ext.Trim();
                    if (!e.StartsWith(".")) e = "." + e;
                    allowedExtensions.Add(e);
                }
            }

            foreach (var entry in allEntries)
            {
                if (ct.IsCancellationRequested) break;
                if (entry.IsDirectory) continue;
                if (string.IsNullOrEmpty(entry.Name)) continue;

                // Extension check
                int dotIndex = entry.Name.LastIndexOf('.');
                if (dotIndex < 0) continue;
                string entryExt = entry.Name.Substring(dotIndex);

                // Skip known binary extensions
                if (IsBinaryExtension(entryExt)) continue;

                // Filter by extensions:
                // 1. User's per-search extension filter (detailed panel) takes priority
                // 2. Otherwise, use settings' content search target extensions
                // 3. If neither is set, skip files without a known text/IFilter-capable extension
                if (extFilter != null)
                {
                    if (!extFilter.Contains(entryExt)) continue;
                }
                else if (allowedExtensions != null)
                {
                    if (!allowedExtensions.Contains(entryExt)) continue;
                }
                else
                {
                    // No filter configured: allow plain text + common IFilter types
                    if (!TextExtractor.IsPlainTextExtension(entryExt)
                        && !IsIFilterExtension(entryExt))
                        continue;
                }

                // Size check
                if (opts.MinSize.HasValue && entry.Size < opts.MinSize.Value) continue;
                if (opts.MaxSize.HasValue && entry.Size > opts.MaxSize.Value) continue;

                // Resolve path and apply folder filter
                string dirPath = _index.ResolvePath(entry);
                if (string.IsNullOrEmpty(dirPath)) continue;

                if (!string.IsNullOrEmpty(folderFilter))
                {
                    bool folderMatch;
                    if (includeSubfolders)
                    {
                        folderMatch = dirPath.StartsWith(folderFilter, StringComparison.OrdinalIgnoreCase)
                            || (folderFilterAlt != null && dirPath.StartsWith(folderFilterAlt, StringComparison.OrdinalIgnoreCase));
                    }
                    else
                    {
                        folderMatch = dirPath.Equals(folderFilter, StringComparison.OrdinalIgnoreCase)
                            || (folderFilterAlt != null && dirPath.Equals(folderFilterAlt, StringComparison.OrdinalIgnoreCase));
                    }
                    if (!folderMatch) continue;
                }

                // Date check
                if (opts.DateFrom.HasValue && entry.ModifiedDate < opts.DateFrom.Value) continue;
                if (opts.DateTo.HasValue && entry.ModifiedDate > opts.DateTo.Value.AddDays(1)) continue;

                string fullPath = dirPath + "\\" + entry.Name;
                targets.Add(new ContentSearchTarget
                {
                    FullPath = fullPath,
                    Name = entry.Name,
                    DirPath = dirPath,
                    Size = entry.Size,
                    Modified = entry.ModifiedDate,
                });
            }

            return targets;
        }

        /// <summary>
        /// Generate a snippet around the match position.
        /// </summary>
        private static string GenerateSnippet(string content, string query, int matchIndex)
        {
            const int contextChars = 60;

            int start = Math.Max(0, matchIndex - contextChars);
            int end = Math.Min(content.Length, matchIndex + query.Length + contextChars);

            string before = start > 0 ? "..." : "";
            string after = end < content.Length ? "..." : "";

            string excerpt = content.Substring(start, end - start);
            // Replace newlines with spaces for single-line display
            excerpt = excerpt.Replace('\r', ' ').Replace('\n', ' ').Replace('\t', ' ');

            return before + excerpt + after;
        }

        private static void ReportProgress(IProgress<ContentSearchProgress> progress,
            int scanned, int total, int matched)
        {
            progress?.Report(new ContentSearchProgress
            {
                ScannedFiles = scanned,
                TotalFiles = total,
                MatchedFiles = matched,
            });
        }

        /// <summary>
        /// Search for query in a single file's content.
        /// Thread-safe: uses Interlocked for counters and ConcurrentBag for results.
        /// </summary>
        private static void SearchFileContent(
            ContentSearchTarget file,
            TextExtractor extractor,
            string query,
            StringComparison comparison,
            int maxResults,
            ConcurrentBag<FileResultItem> results,
            ref int scannedCount,
            ref int matchedCount,
            ref int failedCount,
            int totalFiles,
            IProgress<ContentSearchProgress> progress)
        {
            try
            {
                string content = extractor.ExtractText(file.FullPath);
                if (!string.IsNullOrEmpty(content))
                {
                    int matchIndex = content.IndexOf(query, comparison);
                    if (matchIndex >= 0)
                    {
                        string snippet = GenerateSnippet(content, query, matchIndex);
                        int matched = Interlocked.Increment(ref matchedCount);

                        if (matched <= maxResults)
                        {
                            // Retrieve file size/date from filesystem if not set
                            long size = file.Size;
                            DateTime modified = file.Modified;
                            if (modified == default(DateTime) || size == 0)
                            {
                                try
                                {
                                    var fi = new FileInfo(file.FullPath);
                                    if (fi.Exists)
                                    {
                                        if (size == 0) size = fi.Length;
                                        if (modified == default(DateTime)) modified = fi.LastWriteTime;
                                    }
                                }
                                catch { }
                            }

                            results.Add(new FileResultItem
                            {
                                Name = file.Name,
                                Path = file.DirPath,
                                FullPath = file.FullPath,
                                Size = size,
                                Modified = modified,
                                IsDirectory = false,
                                ContentSnippet = snippet,
                            });
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                int failed = Interlocked.Increment(ref failedCount);
                if (failed <= 10)
                    TNDexplorer.Services.AppLog.Warn("ContentSearch", $"{file.FullPath}: {ex.GetType().Name}");
            }

            int sc = Interlocked.Increment(ref scannedCount);
            if (sc % 50 == 0 || sc == totalFiles)
                ReportProgress(progress, sc, totalFiles, matchedCount);
        }

        /// <summary>
        /// Known binary extensions that should never be content-searched.
        /// </summary>
        private static bool IsBinaryExtension(string ext)
        {
            switch (ext.ToLowerInvariant())
            {
                case ".exe": case ".dll": case ".sys": case ".ocx": case ".com": case ".scr":
                case ".zip": case ".rar": case ".7z": case ".tar": case ".gz": case ".bz2":
                case ".xz": case ".cab": case ".iso":
                case ".png": case ".jpg": case ".jpeg": case ".gif": case ".bmp": case ".ico":
                case ".tiff": case ".tif": case ".svg": case ".webp": case ".psd":
                case ".mp3": case ".wav": case ".flac": case ".aac": case ".ogg": case ".wma":
                case ".mp4": case ".avi": case ".mkv": case ".mov": case ".wmv": case ".flv":
                case ".webm": case ".m4v":
                case ".mdb": case ".accdb": case ".sqlite": case ".db":
                case ".ttf": case ".otf": case ".woff": case ".woff2":
                case ".obj": case ".lib": case ".pdb": case ".dat": case ".bin":
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>
        /// Extensions that can be handled by IFilter (Office, PDF, etc).
        /// </summary>
        private static bool IsIFilterExtension(string ext)
        {
            switch (ext.ToLowerInvariant())
            {
                case ".doc": case ".docx": case ".xls": case ".xlsx":
                case ".ppt": case ".pptx": case ".pdf":
                case ".odt": case ".ods": case ".odp":
                case ".msg": case ".eml":
                case ".vsd": case ".vsdx":
                    return true;
                default:
                    return false;
            }
        }

        private class ContentSearchTarget
        {
            public string FullPath { get; set; }
            public string Name { get; set; }
            public string DirPath { get; set; }
            public long Size { get; set; }
            public DateTime Modified { get; set; }
        }
    }
}
