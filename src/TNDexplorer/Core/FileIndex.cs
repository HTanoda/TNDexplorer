using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace TNDexplorer.Core
{
    public enum SearchFilter
    {
        All,
        FilesOnly,
        FoldersOnly,
    }

    public class FileIndex
    {
        private readonly object _lock = new object();
        private FileEntry[] _entries = new FileEntry[0];
        private int _count;

        // FRN -> FileEntry lookup for path resolution and USN updates
        // Key: (DriveIndex << 56) | (FileReferenceNumber & 0x00FFFFFFFFFFFFFF)
        private readonly ConcurrentDictionary<ulong, FileEntry> _frnLookup
            = new ConcurrentDictionary<ulong, FileEntry>();

        // Drive letter mapping
        private readonly string[] _driveLetters = new string[26];

        // Path cache for resolved full paths
        private readonly ConcurrentDictionary<ulong, string> _pathCache
            = new ConcurrentDictionary<ulong, string>();

        // Exclude patterns (folder names to skip in search results)
        private IReadOnlyList<string> _excludePatterns;

        // Memoization cache for exclude ancestor checks
        // Key: MakeKey(driveIndex, frn), Value: true if excluded
        private readonly ConcurrentDictionary<ulong, bool> _excludeCache
            = new ConcurrentDictionary<ulong, bool>();

        public int TotalCount => _count;

        public void SetDriveLetter(byte driveIndex, string driveLetter)
        {
            _driveLetters[driveIndex] = driveLetter;
        }

        public void AddEntries(byte driveIndex, IList<FileEntry> newEntries)
        {
            lock (_lock)
            {
                int oldCount = _count;
                int newCount = oldCount + newEntries.Count;

                if (newCount > _entries.Length)
                {
                    int capacity = Math.Max(newCount, _entries.Length * 2);
                    capacity = Math.Max(capacity, 1024);
                    var newArray = new FileEntry[capacity];
                    Array.Copy(_entries, newArray, oldCount);
                    _entries = newArray;
                }

                for (int i = 0; i < newEntries.Count; i++)
                {
                    var entry = newEntries[i];
                    entry.DriveIndex = driveIndex;
                    _entries[oldCount + i] = entry;

                    ulong key = MakeKey(driveIndex, entry.FileReferenceNumber);
                    _frnLookup[key] = entry;
                }

                _count = newCount;
            }
        }

        public void AddEntry(byte driveIndex, FileEntry entry)
        {
            lock (_lock)
            {
                entry.DriveIndex = driveIndex;

                if (_count >= _entries.Length)
                {
                    int capacity = Math.Max(_entries.Length * 2, 1024);
                    var newArray = new FileEntry[capacity];
                    Array.Copy(_entries, newArray, _count);
                    _entries = newArray;
                }

                _entries[_count] = entry;
                _count++;

                ulong key = MakeKey(driveIndex, entry.FileReferenceNumber);
                _frnLookup[key] = entry;
            }

            InvalidatePathCache(driveIndex, entry.FileReferenceNumber);
        }

        public void RemoveEntry(byte driveIndex, ulong fileReferenceNumber)
        {
            ulong key = MakeKey(driveIndex, fileReferenceNumber);
            _frnLookup.TryRemove(key, out _);

            lock (_lock)
            {
                for (int i = 0; i < _count; i++)
                {
                    if (_entries[i] != null
                        && _entries[i].DriveIndex == driveIndex
                        && _entries[i].FileReferenceNumber == fileReferenceNumber)
                    {
                        // Swap with last entry for O(1) removal
                        _count--;
                        _entries[i] = _entries[_count];
                        _entries[_count] = null;
                        break;
                    }
                }
            }

            InvalidatePathCache(driveIndex, fileReferenceNumber);
        }

        public List<FileEntry> GetEntriesForDrive(byte driveIndex)
        {
            lock (_lock)
            {
                var result = new List<FileEntry>();
                for (int i = 0; i < _count; i++)
                {
                    if (_entries[i] != null && _entries[i].DriveIndex == driveIndex)
                        result.Add(_entries[i]);
                }
                return result;
            }
        }

        public void RemoveEntriesForDrive(byte driveIndex)
        {
            lock (_lock)
            {
                // Remove from FRN lookup
                var keysToRemove = new List<ulong>();
                foreach (var kvp in _frnLookup)
                {
                    if (kvp.Value.DriveIndex == driveIndex)
                        keysToRemove.Add(kvp.Key);
                }
                foreach (var key in keysToRemove)
                {
                    _frnLookup.TryRemove(key, out _);
                    _pathCache.TryRemove(key, out _);
                }

                // Compact the entries array
                int writeIdx = 0;
                for (int i = 0; i < _count; i++)
                {
                    if (_entries[i] != null && _entries[i].DriveIndex != driveIndex)
                    {
                        _entries[writeIdx] = _entries[i];
                        writeIdx++;
                    }
                }
                for (int i = writeIdx; i < _count; i++)
                {
                    _entries[i] = null;
                }
                _count = writeIdx;
            }
        }

        public void UpdateEntry(byte driveIndex, ulong fileReferenceNumber, FileEntry updated)
        {
            ulong key = MakeKey(driveIndex, fileReferenceNumber);
            updated.DriveIndex = driveIndex;
            _frnLookup[key] = updated;

            lock (_lock)
            {
                for (int i = 0; i < _count; i++)
                {
                    if (_entries[i] != null
                        && _entries[i].DriveIndex == driveIndex
                        && _entries[i].FileReferenceNumber == fileReferenceNumber)
                    {
                        _entries[i] = updated;
                        break;
                    }
                }
            }

            InvalidatePathCache(driveIndex, fileReferenceNumber);
        }

        public List<FileEntry> Search(string query, SearchFilter filter, int maxResults,
            CancellationToken ct, bool caseSensitive = false,
            string extensionFilter = null, string folderPath = null,
            bool includeSubfolders = true, bool excludeHidden = false, bool excludeSystem = false)
        {
            // Parse the query using the advanced parser (AND/OR/NOT/ext: syntax)
            var parsedQuery = SearchQueryParser.Parse(query);

            bool hasExtFilter = !string.IsNullOrEmpty(extensionFilter);
            bool hasFolderFilter = !string.IsNullOrEmpty(folderPath);
            bool queryHasTerms = !parsedQuery.IsEmpty;

            // Nothing to search for
            if (!queryHasTerms && !hasExtFilter && !hasFolderFilter)
                return new List<FileEntry>();

            var results = new ConcurrentBag<FileEntry>();
            int resultCount = 0; // Atomic counter (Interlocked) to avoid O(n) ConcurrentBag.Count
            int currentCount = _count;
            var localEntries = _entries;

            var comparison = caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

            // Preprocess advanced panel extension filter (may contain multiple extensions separated by ;)
            string[] advExtFilters = null;
            if (hasExtFilter)
            {
                var exts = extensionFilter.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries);
                advExtFilters = new string[exts.Length];
                for (int e = 0; e < exts.Length; e++)
                {
                    string ext = exts[e].TrimStart('*').Trim();
                    if (!ext.StartsWith("."))
                        ext = "." + ext;
                    advExtFilters[e] = ext;
                }
            }

            // Preprocess folder path filter - normalize
            string folderFilter = null;
            string folderFilterAlt = null; // Alternative folder filter (UNC path if drive letter, or vice versa)
            if (hasFolderFilter)
            {
                folderFilter = folderPath.TrimEnd('\\').TrimEnd('/');

                // If the folder path starts with a drive letter (e.g. K:\...),
                // also try matching via UNC path for mapped network drives
                if (folderFilter.Length >= 2 && folderFilter[1] == ':')
                {
                    string driveLetter = folderFilter.Substring(0, 2); // e.g. "K:"
                    string uncBase = NetworkDriveDetector.GetUncPath(driveLetter);
                    if (!string.IsNullOrEmpty(uncBase))
                    {
                        // Replace drive letter prefix with UNC path
                        // e.g. "K:\foo\bar" -> "\\server\share\foo\bar"
                        string remainder = folderFilter.Length > 2 ? folderFilter.Substring(2) : "";
                        folderFilterAlt = uncBase.TrimEnd('\\') + remainder;
                    }
                }
                // If the folder path starts with UNC (\\...), also try matching via drive letter
                else if (folderFilter.StartsWith("\\\\"))
                {
                    // Check all mapped drives to find one that maps to this UNC prefix
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

            // Pre-extract clauses for use in parallel loop
            var clauses = parsedQuery.AndClauses;

            Parallel.ForEach(
                System.Collections.Concurrent.Partitioner.Create(0, currentCount),
                new ParallelOptions
                {
                    CancellationToken = ct,
                    MaxDegreeOfParallelism = Environment.ProcessorCount
                },
                (range, loopState) =>
                {
                    for (int i = range.Item1; i < range.Item2; i++)
                    {
                        if (ct.IsCancellationRequested || Interlocked.CompareExchange(ref resultCount, 0, 0) >= maxResults)
                        {
                            loopState.Break();
                            return;
                        }

                        var entry = localEntries[i];
                        if (entry == null) continue;

                        // Exclude hidden / system files
                        if (excludeHidden && entry.IsHidden) continue;
                        if (excludeSystem && entry.IsSystem) continue;

                        // Exclude entries in excluded folders (e.g., $Recycle.Bin)
                        var excludePatterns = _excludePatterns;
                        if (excludePatterns != null && excludePatterns.Count > 0)
                        {
                            // Check if directory itself matches an exclude pattern
                            if (entry.IsDirectory)
                            {
                                bool selfExcluded = false;
                                for (int p = 0; p < excludePatterns.Count; p++)
                                {
                                    if (string.Equals(entry.Name, excludePatterns[p], StringComparison.OrdinalIgnoreCase))
                                    { selfExcluded = true; break; }
                                }
                                if (selfExcluded) continue;
                            }
                            // Check if any ancestor directory matches an exclude pattern
                            if (IsAncestorExcluded(entry.DriveIndex, entry.ParentFileReferenceNumber))
                                continue;
                        }

                        // Apply filter
                        if (filter == SearchFilter.FilesOnly && entry.IsDirectory) continue;
                        if (filter == SearchFilter.FoldersOnly && !entry.IsDirectory) continue;

                        // Advanced panel extension filter - exclude directories
                        if (advExtFilters != null)
                        {
                            if (entry.IsDirectory) continue;
                            bool extMatch = false;
                            for (int e = 0; e < advExtFilters.Length; e++)
                            {
                                if (entry.Name.EndsWith(advExtFilters[e], StringComparison.OrdinalIgnoreCase))
                                {
                                    extMatch = true;
                                    break;
                                }
                            }
                            if (!extMatch) continue;
                        }

                        // Match parsed query clauses (AND logic between clauses, OR within each clause)
                        if (queryHasTerms)
                        {
                            bool allClausesMatch = true;
                            for (int c = 0; c < clauses.Count; c++)
                            {
                                var clause = clauses[c];
                                bool anyTermMatch = false;

                                for (int t = 0; t < clause.OrTerms.Count; t++)
                                {
                                    if (EvaluateTerm(entry, clause.OrTerms[t], comparison))
                                    {
                                        anyTermMatch = true;
                                        break;
                                    }
                                }

                                if (!anyTermMatch)
                                {
                                    allClausesMatch = false;
                                    break;
                                }
                            }
                            if (!allClausesMatch) continue;
                        }

                        // Folder path filter (check after name match to avoid expensive path resolution)
                        if (folderFilter != null)
                        {
                            string entryPath = ResolvePath(entry);
                            bool folderMatch;
                            if (includeSubfolders)
                            {
                                folderMatch = entryPath.StartsWith(folderFilter, StringComparison.OrdinalIgnoreCase)
                                    || (folderFilterAlt != null && entryPath.StartsWith(folderFilterAlt, StringComparison.OrdinalIgnoreCase));
                            }
                            else
                            {
                                folderMatch = entryPath.Equals(folderFilter, StringComparison.OrdinalIgnoreCase)
                                    || (folderFilterAlt != null && entryPath.Equals(folderFilterAlt, StringComparison.OrdinalIgnoreCase));
                            }
                            if (!folderMatch) continue;
                        }

                        results.Add(entry);
                        Interlocked.Increment(ref resultCount);
                    }
                });

            var list = new List<FileEntry>(results);
            if (list.Count > maxResults)
                list.RemoveRange(maxResults, list.Count - maxResults);
            return list;
        }

        private static bool EvaluateTerm(FileEntry entry, SearchTerm term, StringComparison comparison)
        {
            bool result;

            switch (term.Type)
            {
                case TermType.Extension:
                    if (entry.IsDirectory)
                    {
                        result = false;
                    }
                    else
                    {
                        result = false;
                        for (int e = 0; e < term.Extensions.Length; e++)
                        {
                            if (entry.Name.EndsWith(term.Extensions[e], StringComparison.OrdinalIgnoreCase))
                            {
                                result = true;
                                break;
                            }
                        }
                    }
                    break;

                case TermType.Keyword:
                    result = entry.Name.IndexOf(term.Value, comparison) >= 0;
                    break;

                default:
                    result = false;
                    break;
            }

            return term.IsNegated ? !result : result;
        }

        public string ResolvePath(FileEntry entry)
        {
            ulong key = MakeKey(entry.DriveIndex, entry.FileReferenceNumber);
            if (_pathCache.TryGetValue(key, out string cached))
                return cached;

            var parts = new Stack<string>();
            ulong currentFrn = entry.ParentFileReferenceNumber;
            byte driveIndex = entry.DriveIndex;

            int maxDepth = 256; // prevent infinite loops
            while (currentFrn != 0 && maxDepth-- > 0)
            {
                ulong parentKey = MakeKey(driveIndex, currentFrn);
                if (!_frnLookup.TryGetValue(parentKey, out FileEntry parent))
                    break;

                parts.Push(parent.Name);
                currentFrn = parent.ParentFileReferenceNumber;
            }

            string driveLetter = _driveLetters[driveIndex] ?? "?:";
            string path = parts.Count == 0
                ? driveLetter
                : driveLetter + "\\" + string.Join("\\", parts);

            _pathCache[key] = path;
            return path;
        }

        public string ResolveFullPath(FileEntry entry)
        {
            string dir = ResolvePath(entry);
            return dir + "\\" + entry.Name;
        }

        /// <summary>
        /// Get a snapshot of all entries in the index.
        /// Returns a copy of the internal array for safe enumeration.
        /// </summary>
        public FileEntry[] GetAllEntries()
        {
            lock (_lock)
            {
                var snapshot = new FileEntry[_count];
                Array.Copy(_entries, snapshot, _count);
                return snapshot;
            }
        }

        /// <summary>
        /// Set folder name patterns to exclude from search results.
        /// Entries whose name (for directories) or any ancestor directory name
        /// matches a pattern will be excluded.
        /// </summary>
        public void SetExcludePatterns(IReadOnlyList<string> patterns)
        {
            _excludePatterns = (patterns != null && patterns.Count > 0) ? patterns : null;
            _excludeCache.Clear();
        }

        /// <summary>
        /// Check if any ancestor directory of the given parent FRN matches an exclude pattern.
        /// Uses memoization for performance (sibling entries share ancestors).
        /// </summary>
        private bool IsAncestorExcluded(byte driveIndex, ulong parentFrn)
        {
            ulong current = parentFrn;
            int maxDepth = 64;
            var chain = new List<ulong>();

            while (current != 0 && maxDepth-- > 0)
            {
                ulong key = MakeKey(driveIndex, current);

                // Check memoization cache
                if (_excludeCache.TryGetValue(key, out bool cached))
                {
                    // Propagate cached result to all entries in the chain
                    foreach (var chainKey in chain)
                        _excludeCache[chainKey] = cached;
                    return cached;
                }

                if (!_frnLookup.TryGetValue(key, out FileEntry parent))
                    break;

                // Check if this ancestor matches any exclude pattern
                bool isExcluded = false;
                var patterns = _excludePatterns;
                for (int i = 0; i < patterns.Count; i++)
                {
                    if (string.Equals(parent.Name, patterns[i], StringComparison.OrdinalIgnoreCase))
                    {
                        isExcluded = true;
                        break;
                    }
                }

                if (isExcluded)
                {
                    _excludeCache[key] = true;
                    foreach (var chainKey in chain)
                        _excludeCache[chainKey] = true;
                    return true;
                }

                chain.Add(key);

                if (parent.ParentFileReferenceNumber == current)
                    break;
                current = parent.ParentFileReferenceNumber;
            }

            // Not excluded — cache negative result for all chain entries
            foreach (var chainKey in chain)
                _excludeCache[chainKey] = false;
            return false;
        }

        public void Clear()
        {
            lock (_lock)
            {
                _entries = new FileEntry[0];
                _count = 0;
                _frnLookup.Clear();
                _pathCache.Clear();
                _excludeCache.Clear();
            }
        }

        public void ClearDrive(byte driveIndex)
        {
            lock (_lock)
            {
                int writeIndex = 0;
                for (int i = 0; i < _count; i++)
                {
                    if (_entries[i] != null && _entries[i].DriveIndex != driveIndex)
                    {
                        _entries[writeIndex] = _entries[i];
                        writeIndex++;
                    }
                    else if (_entries[i] != null)
                    {
                        ulong key = MakeKey(driveIndex, _entries[i].FileReferenceNumber);
                        _frnLookup.TryRemove(key, out _);
                        _pathCache.TryRemove(key, out _);
                    }
                }
                for (int i = writeIndex; i < _count; i++)
                    _entries[i] = null;
                _count = writeIndex;
            }
        }

        private static ulong MakeKey(byte driveIndex, ulong frn)
        {
            // Use upper 8 bits for drive index, lower 56 bits for FRN
            return ((ulong)driveIndex << 56) | (frn & 0x00FFFFFFFFFFFFFF);
        }

        private void InvalidatePathCache(byte driveIndex, ulong frn)
        {
            ulong key = MakeKey(driveIndex, frn);
            _pathCache.TryRemove(key, out _);
        }
    }
}
