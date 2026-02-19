using System;
using System.Collections.Generic;
using System.IO;
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
        /// Search file contents using the full-text search index (FTS5).
        /// Returns results with content snippets.
        /// </summary>
        public async Task<List<FileResultItem>> ContentSearchAsync(
            string query,
            SearchFilter filter,
            int maxResults,
            CancellationToken externalCt,
            FullTextIndex ftsIndex,
            AdvancedSearchOptions advancedOptions = null)
        {
            if (ftsIndex == null || string.IsNullOrWhiteSpace(query) || query.Length < ftsIndex.MinQueryLength)
                return new List<FileResultItem>();

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
                    // Search FTS index
                    var ftsResults = ftsIndex.Search(query, maxResults * 2); // Over-fetch for filtering

                    var results = new List<FileResultItem>(Math.Min(ftsResults.Count, maxResults));

                    foreach (var ftsResult in ftsResults)
                    {
                        if (newCts.Token.IsCancellationRequested || results.Count >= maxResults)
                            break;

                        string filePath = ftsResult.FilePath;
                        if (string.IsNullOrEmpty(filePath))
                            continue;

                        try
                        {
                            string name = Path.GetFileName(filePath);
                            string dirPath = Path.GetDirectoryName(filePath) ?? "";

                            // Apply filter type
                            if (filter == SearchFilter.FoldersOnly)
                                continue; // Content search never returns folders

                            // Get file info
                            long size = 0;
                            DateTime modified = default(DateTime);
                            bool exists = false;

                            try
                            {
                                var fi = new FileInfo(filePath);
                                if (fi.Exists)
                                {
                                    size = fi.Length;
                                    modified = fi.LastWriteTime;
                                    exists = true;
                                }
                            }
                            catch { }

                            if (!exists) continue;

                            // Apply advanced filters
                            if (advancedOptions != null)
                            {
                                // Extension filter
                                if (!string.IsNullOrEmpty(advancedOptions.ExtensionFilter))
                                {
                                    string ext = Path.GetExtension(name);
                                    var extFilters = advancedOptions.ExtensionFilter
                                        .Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries);
                                    bool extMatch = false;
                                    foreach (var ef in extFilters)
                                    {
                                        string target = ef.TrimStart('*').Trim();
                                        if (!target.StartsWith(".")) target = "." + target;
                                        if (ext.Equals(target, StringComparison.OrdinalIgnoreCase))
                                        {
                                            extMatch = true;
                                            break;
                                        }
                                    }
                                    if (!extMatch) continue;
                                }

                                // Folder filter
                                if (!string.IsNullOrEmpty(advancedOptions.FolderPath))
                                {
                                    string folderFilter = advancedOptions.FolderPath.TrimEnd('\\', '/');
                                    if (advancedOptions.IncludeSubfolders)
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

                                // Date filter
                                if (advancedOptions.DateFrom.HasValue && modified < advancedOptions.DateFrom.Value)
                                    continue;
                                if (advancedOptions.DateTo.HasValue && modified > advancedOptions.DateTo.Value.AddDays(1))
                                    continue;

                                // Size filter
                                if (advancedOptions.MinSize.HasValue && size < advancedOptions.MinSize.Value)
                                    continue;
                                if (advancedOptions.MaxSize.HasValue && size > advancedOptions.MaxSize.Value)
                                    continue;
                            }

                            results.Add(new FileResultItem
                            {
                                Name = name,
                                Path = dirPath,
                                FullPath = filePath,
                                Size = size,
                                Modified = modified,
                                IsDirectory = false,
                                ContentSnippet = ftsResult.Snippet,
                            });
                        }
                        catch { }
                    }

                    return results;
                }, newCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return new List<FileResultItem>();
            }
        }
    }
}
