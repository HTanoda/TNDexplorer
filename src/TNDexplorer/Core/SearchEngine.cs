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
                            Size = size,
                            Modified = modified,
                            IsDirectory = entry.IsDirectory,
                        });
                    }

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
            IProgress<ContentSearchProgress> progress = null)
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
                    var targetFiles = CollectTargetFiles(advancedOptions, ct);
                    int totalFiles = targetFiles.Count;

                    var results = new ConcurrentBag<FileResultItem>();
                    int scannedCount = 0;
                    int matchedCount = 0;
                    var comparison = advancedOptions.CaseSensitive
                        ? StringComparison.Ordinal
                        : StringComparison.OrdinalIgnoreCase;

                    // Step 2: Parallel search through file contents
                    var options = new ParallelOptions
                    {
                        MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount),
                        CancellationToken = ct,
                    };

                    try
                    {
                        Parallel.ForEach(targetFiles, options, (file, loopState) =>
                        {
                            if (ct.IsCancellationRequested || results.Count >= maxResults)
                            {
                                loopState.Break();
                                return;
                            }

                            try
                            {
                                // Extract text content
                                string content = extractor.ExtractText(file.FullPath);
                                if (string.IsNullOrEmpty(content))
                                {
                                    Interlocked.Increment(ref scannedCount);
                                    ReportProgress(progress, scannedCount, totalFiles, matchedCount);
                                    return;
                                }

                                // Search for query in content
                                int matchIndex = content.IndexOf(query, comparison);
                                if (matchIndex >= 0)
                                {
                                    string snippet = GenerateSnippet(content, query, matchIndex);
                                    int matched = Interlocked.Increment(ref matchedCount);

                                    if (matched <= maxResults)
                                    {
                                        results.Add(new FileResultItem
                                        {
                                            Name = file.Name,
                                            Path = file.DirPath,
                                            FullPath = file.FullPath,
                                            Size = file.Size,
                                            Modified = file.Modified,
                                            IsDirectory = false,
                                            ContentSnippet = snippet,
                                        });
                                    }
                                }
                            }
                            catch { }

                            Interlocked.Increment(ref scannedCount);
                            if (scannedCount % 50 == 0 || scannedCount == totalFiles)
                                ReportProgress(progress, scannedCount, totalFiles, matchedCount);
                        });
                    }
                    catch (OperationCanceledException) { }

                    // Final progress report
                    ReportProgress(progress, scannedCount, totalFiles, matchedCount);

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
        private List<ContentSearchTarget> CollectTargetFiles(AdvancedSearchOptions opts, CancellationToken ct)
        {
            var targets = new List<ContentSearchTarget>();
            var allEntries = _index.GetAllEntries();
            if (allEntries == null) return targets;

            string folderFilter = opts.FolderPath?.TrimEnd('\\', '/') ?? "";
            bool includeSubfolders = opts.IncludeSubfolders;

            // Parse extension filter from advanced options
            HashSet<string> extFilter = null;
            if (!string.IsNullOrWhiteSpace(opts.ExtensionFilter))
            {
                extFilter = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var ef in opts.ExtensionFilter.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    string ext = ef.TrimStart('*').Trim();
                    if (!ext.StartsWith(".")) ext = "." + ext;
                    extFilter.Add(ext);
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

                // If user specified extension filter, use that; otherwise use TextExtractor-supported extensions
                if (extFilter != null)
                {
                    if (!extFilter.Contains(entryExt)) continue;
                }
                else
                {
                    if (!TextExtractor.IsPlainTextExtension(entryExt)) continue;
                }

                // Size check
                if (opts.MinSize.HasValue && entry.Size < opts.MinSize.Value) continue;
                if (opts.MaxSize.HasValue && entry.Size > opts.MaxSize.Value) continue;

                // Resolve path and apply folder filter
                string dirPath = _index.ResolvePath(entry);
                if (string.IsNullOrEmpty(dirPath)) continue;

                if (!string.IsNullOrEmpty(folderFilter))
                {
                    if (includeSubfolders)
                    {
                        if (!dirPath.StartsWith(folderFilter, StringComparison.OrdinalIgnoreCase))
                            continue;
                    }
                    else
                    {
                        if (!dirPath.Equals(folderFilter, StringComparison.OrdinalIgnoreCase))
                            continue;
                    }
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
