using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TNDexplorer.Models;
using TNDexplorer.Native;
using TNDexplorer.Services;

namespace TNDexplorer.Core
{
    public enum IndexingState
    {
        NotStarted,
        Indexing,
        Ready,
        Error,
    }

    public class IndexingProgressEventArgs : EventArgs
    {
        public string Message { get; set; }
        public int TotalEntries { get; set; }
        public IndexingState State { get; set; }
    }

    public class IndexManager : IDisposable
    {
        private readonly FileIndex _index;
        private readonly SearchEngine _searchEngine;
        private readonly NetworkDriveDetector _driveDetector;
        private readonly SettingsService _settingsService;
        private readonly NetworkIndexCache _networkCache;

        private readonly List<MftReader> _mftReaders = new List<MftReader>();
        private readonly List<UsnJournalMonitor> _usnMonitors = new List<UsnJournalMonitor>();
        private CancellationTokenSource _cts;
        private byte _nextDriveIndex;
        private bool _disposed;

        // Track network drive assignments for rescan
        private readonly Dictionary<string, byte> _networkDriveIndexMap = new Dictionary<string, byte>();
        // Track custom folder assignments for rescan
        private readonly Dictionary<string, byte> _customFolderIndexMap = new Dictionary<string, byte>(StringComparer.OrdinalIgnoreCase);
        private AppSettings _currentSettings;
        private Timer _rescanTimer;

        // Content search (grep-style): TextExtractor for on-demand file reading
        private TextExtractor _textExtractor;

        public FileIndex Index => _index;
        public SearchEngine SearchEngine => _searchEngine;
        public TextExtractor TextExtractor => _textExtractor;
        public IndexingState State { get; private set; } = IndexingState.NotStarted;
        public event EventHandler<IndexingProgressEventArgs> ProgressChanged;

        public IndexManager(SettingsService settingsService)
        {
            _index = new FileIndex();
            _searchEngine = new SearchEngine(_index);
            _driveDetector = new NetworkDriveDetector();
            _settingsService = settingsService;
            _networkCache = new NetworkIndexCache();
        }

        public async Task InitializeAsync(AppSettings settings)
        {
            _cts = new CancellationTokenSource();
            _currentSettings = settings;
            State = IndexingState.Indexing;

            try
            {
                // Index local NTFS drives
                var localTasks = new List<Task>();
                foreach (var drive in settings.LocalDrives.Where(d => d.Enabled))
                {
                    byte driveIndex = _nextDriveIndex++;
                    _index.SetDriveLetter(driveIndex, drive.DriveLetter);

                    localTasks.Add(Task.Run(() => IndexLocalDrive(drive.DriveLetter, driveIndex, _cts.Token)));
                }

                // Index network drives (with cache support)
                var networkTasks = new List<Task>();
                foreach (var drive in settings.NetworkDrives.Where(d => d.Enabled))
                {
                    byte driveIndex = _nextDriveIndex++;
                    string rootPath = drive.DriveLetter + "\\";
                    _index.SetDriveLetter(driveIndex, drive.DriveLetter);
                    _networkDriveIndexMap[drive.DriveLetter] = driveIndex;

                    networkTasks.Add(Task.Run(() => IndexNetworkDriveWithCache(
                        rootPath, drive.DriveLetter, driveIndex,
                        settings.ExcludePatterns, _cts.Token)));
                }

                // Index custom folders (filesystem enumeration, with cache support)
                var customFolderTasks = new List<Task>();
                if (settings.CustomFolders != null)
                {
                    foreach (var folder in settings.CustomFolders.Where(f => f.Enabled))
                    {
                        if (string.IsNullOrWhiteSpace(folder.FolderPath)) continue;
                        if (!System.IO.Directory.Exists(folder.FolderPath)) continue;

                        byte driveIndex = _nextDriveIndex++;
                        // Use folder path as a pseudo drive letter
                        string pseudoDrive = folder.FolderPath;
                        _index.SetDriveLetter(driveIndex, pseudoDrive);
                        _customFolderIndexMap[folder.FolderPath] = driveIndex;

                        string rootPath = folder.FolderPath;
                        customFolderTasks.Add(Task.Run(() => IndexCustomFolderWithCache(
                            rootPath, driveIndex,
                            settings.ExcludePatterns, _cts.Token)));
                    }
                }

                await Task.WhenAll(localTasks.Concat(networkTasks).Concat(customFolderTasks)).ConfigureAwait(false);

                State = IndexingState.Ready;
                RaiseProgress("インデックス構築完了", _index.TotalCount, IndexingState.Ready);

                // Start drive change monitoring
                _ = _driveDetector.MonitorDriveChanges(OnDriveChanged,
                    TimeSpan.FromSeconds(30), _cts.Token);

                // Start periodic rescan if configured (applies to both network drives AND custom folders)
                StartRescanTimer(settings);

                // Initialize TextExtractor for content search (grep-style)
                InitializeTextExtractor(settings);
            }
            catch (Exception ex)
            {
                State = IndexingState.Error;
                RaiseProgress($"エラー: {ex.Message}", _index.TotalCount, IndexingState.Error);
            }
        }

        private void StartRescanTimer(AppSettings settings)
        {
            bool hasNetworkDrives = settings.NetworkDrives.Any(d => d.Enabled);
            bool hasCustomFolders = settings.CustomFolders != null && settings.CustomFolders.Any(f => f.Enabled);

            if (settings.NetworkRescanIntervalMinutes > 0 && (hasNetworkDrives || hasCustomFolders))
            {
                int intervalMs = settings.NetworkRescanIntervalMinutes * 60 * 1000;
                _rescanTimer = new Timer(OnRescanTimerTick, null, intervalMs, intervalMs);
            }
        }

        private void IndexLocalDrive(string driveLetter, byte driveIndex, CancellationToken ct)
        {
            RaiseProgress($"{driveLetter} をスキャン中...", _index.TotalCount, IndexingState.Indexing);

            var mftReader = new MftReader(driveLetter, driveIndex);
            lock (_mftReaders) { _mftReaders.Add(mftReader); }

            try
            {
                var progress = new Progress<MftScanProgress>(p =>
                {
                    RaiseProgress(
                        $"{p.DriveLetter} スキャン中... {p.EntriesFound:N0} 件",
                        _index.TotalCount,
                        IndexingState.Indexing);
                });

                var entries = mftReader.EnumerateAllEntries(ct, progress);
                _index.AddEntries(driveIndex, entries);

                RaiseProgress(
                    $"{driveLetter} 完了 ({entries.Count:N0} 件)",
                    _index.TotalCount,
                    IndexingState.Indexing);

                // Start USN Journal monitoring
                var journalData = mftReader.QueryOrCreateJournal();
                var monitor = new UsnJournalMonitor(
                    mftReader.VolumeHandle,
                    driveIndex,
                    _index,
                    journalData.UsnJournalID,
                    journalData.NextUsn);

                lock (_usnMonitors) { _usnMonitors.Add(monitor); }
                monitor.FileChanged += (s, e) =>
                {
                    RaiseProgress(null, _index.TotalCount, IndexingState.Ready);
                };
                monitor.Start();
            }
            catch (Exception ex)
            {
                RaiseProgress(
                    $"{driveLetter} エラー: {ex.Message}",
                    _index.TotalCount,
                    IndexingState.Indexing);
            }
        }

        /// <summary>
        /// Maximum cache age before it is considered too stale to use.
        /// Caches older than this will be discarded and a full rescan performed.
        /// </summary>
        private static readonly TimeSpan MaxCacheAge = TimeSpan.FromDays(7);

        /// <summary>
        /// Cache age threshold below which background rescan is skipped at startup.
        /// If cache was updated within this period, it is considered fresh enough.
        /// </summary>
        private static readonly TimeSpan FreshCacheThreshold = TimeSpan.FromHours(1);

        private void IndexNetworkDriveWithCache(string rootPath, string driveLetter,
            byte driveIndex, IReadOnlyList<string> excludePatterns, CancellationToken ct)
        {
            // Check cache freshness before loading
            var cacheTimestamp = _networkCache.GetCacheTimestamp(driveLetter);
            bool cacheIsFresh = false;

            if (cacheTimestamp.HasValue)
            {
                var cacheAge = DateTime.Now - cacheTimestamp.Value;

                // Cache too old: discard and do full scan
                if (cacheAge > MaxCacheAge)
                {
                    RaiseProgress($"{driveLetter} キャッシュが古いため再スキャンします...", _index.TotalCount, IndexingState.Indexing);
                    _networkCache.Delete(driveLetter);
                    IndexNetworkDrive(rootPath, driveLetter, driveIndex, excludePatterns, ct);
                    return;
                }

                // Cache is recent enough to skip immediate background rescan
                if (cacheAge <= FreshCacheThreshold)
                {
                    cacheIsFresh = true;
                }
            }

            // Try loading from cache
            var cached = _networkCache.Load(driveLetter);
            if (cached != null && cached.Count > 0)
            {
                RaiseProgress($"{driveLetter} キャッシュから読み込み中...", _index.TotalCount, IndexingState.Indexing);

                var entries = new List<FileEntry>();
                foreach (var c in cached)
                {
                    var flags = FileEntryFlags.Network;
                    if (c.IsDir) flags |= FileEntryFlags.Directory;

                    entries.Add(new FileEntry
                    {
                        FileReferenceNumber = c.FRN,
                        ParentFileReferenceNumber = c.ParentFRN,
                        Name = c.Name,
                        Size = c.Size,
                        ModifiedDate = c.Modified,
                        Flags = flags,
                        DriveIndex = driveIndex,
                    });
                }

                _index.AddEntries(driveIndex, entries);
                RaiseProgress(
                    $"{driveLetter} キャッシュ読込完了 ({entries.Count:N0} 件)",
                    _index.TotalCount,
                    IndexingState.Indexing);

                // Only schedule background rescan if cache is not fresh
                if (!cacheIsFresh)
                {
                    Task.Run(() => RescanNetworkDrive(rootPath, driveLetter, driveIndex, excludePatterns, ct));
                }
                return;
            }

            // No cache: full scan
            IndexNetworkDrive(rootPath, driveLetter, driveIndex, excludePatterns, ct);
        }

        private void IndexNetworkDrive(string rootPath, string driveLetter,
            byte driveIndex, IReadOnlyList<string> excludePatterns, CancellationToken ct)
        {
            RaiseProgress($"{rootPath} をスキャン中...", _index.TotalCount, IndexingState.Indexing);

            var scanner = new NetworkDriveScanner();

            try
            {
                var progress = new Progress<NetworkScanProgress>(p =>
                {
                    RaiseProgress(
                        $"{p.RootPath} スキャン中... {p.EntriesFound:N0} 件",
                        _index.TotalCount,
                        IndexingState.Indexing);
                });

                var entries = scanner.ScanDrive(rootPath, driveIndex, excludePatterns, ct, progress);
                _index.AddEntries(driveIndex, entries);

                // Save to cache
                _networkCache.Save(driveLetter, entries);

                RaiseProgress(
                    $"{rootPath} 完了 ({entries.Count:N0} 件)",
                    _index.TotalCount,
                    IndexingState.Indexing);
            }
            catch (Exception ex)
            {
                RaiseProgress(
                    $"{rootPath} エラー: {ex.Message}",
                    _index.TotalCount,
                    IndexingState.Indexing);
            }
        }

        private void RescanNetworkDrive(string rootPath, string driveLetter,
            byte driveIndex, IReadOnlyList<string> excludePatterns, CancellationToken ct)
        {
            var scanner = new NetworkDriveScanner();

            try
            {
                var entries = scanner.ScanDrive(rootPath, driveIndex, excludePatterns, ct);

                // Replace index entries for this drive
                _index.RemoveEntriesForDrive(driveIndex);
                _index.AddEntries(driveIndex, entries);

                // Update cache
                _networkCache.Save(driveLetter, entries);

                RaiseProgress(
                    $"{driveLetter} リスキャン完了 ({entries.Count:N0} 件)",
                    _index.TotalCount,
                    IndexingState.Ready);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Network rescan failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Index a custom folder with cache support (same pattern as network drives).
        /// Uses the folder path as the cache key.
        /// </summary>
        private void IndexCustomFolderWithCache(string rootPath, byte driveIndex,
            IReadOnlyList<string> excludePatterns, CancellationToken ct)
        {
            string cacheKey = GetCustomFolderCacheKey(rootPath);

            // Check cache freshness
            var cacheTimestamp = _networkCache.GetCacheTimestamp(cacheKey);
            bool cacheIsFresh = false;

            if (cacheTimestamp.HasValue)
            {
                var cacheAge = DateTime.Now - cacheTimestamp.Value;

                if (cacheAge > MaxCacheAge)
                {
                    RaiseProgress($"{rootPath} キャッシュが古いため再スキャンします...", _index.TotalCount, IndexingState.Indexing);
                    _networkCache.Delete(cacheKey);
                    IndexCustomFolder(rootPath, cacheKey, driveIndex, excludePatterns, ct);
                    return;
                }

                if (cacheAge <= FreshCacheThreshold)
                {
                    cacheIsFresh = true;
                }
            }

            // Try loading from cache
            var cached = _networkCache.Load(cacheKey);
            if (cached != null && cached.Count > 0)
            {
                RaiseProgress($"{rootPath} キャッシュから読み込み中...", _index.TotalCount, IndexingState.Indexing);

                var entries = new List<FileEntry>();
                foreach (var c in cached)
                {
                    var flags = FileEntryFlags.Network;
                    if (c.IsDir) flags |= FileEntryFlags.Directory;

                    entries.Add(new FileEntry
                    {
                        FileReferenceNumber = c.FRN,
                        ParentFileReferenceNumber = c.ParentFRN,
                        Name = c.Name,
                        Size = c.Size,
                        ModifiedDate = c.Modified,
                        Flags = flags,
                        DriveIndex = driveIndex,
                    });
                }

                _index.AddEntries(driveIndex, entries);
                RaiseProgress(
                    $"{rootPath} キャッシュ読込完了 ({entries.Count:N0} 件)",
                    _index.TotalCount,
                    IndexingState.Indexing);

                if (!cacheIsFresh)
                {
                    Task.Run(() => RescanCustomFolder(rootPath, cacheKey, driveIndex, excludePatterns, ct));
                }
                return;
            }

            // No cache: full scan
            IndexCustomFolder(rootPath, cacheKey, driveIndex, excludePatterns, ct);
        }

        private void IndexCustomFolder(string rootPath, string cacheKey, byte driveIndex,
            IReadOnlyList<string> excludePatterns, CancellationToken ct)
        {
            RaiseProgress($"{rootPath} をスキャン中...", _index.TotalCount, IndexingState.Indexing);

            var scanner = new NetworkDriveScanner();

            try
            {
                string rootWithSlash = rootPath.EndsWith("\\") ? rootPath : rootPath + "\\";
                var progress = new Progress<NetworkScanProgress>(p =>
                {
                    RaiseProgress(
                        $"{rootPath} スキャン中... {p.EntriesFound:N0} 件",
                        _index.TotalCount,
                        IndexingState.Indexing);
                });

                var entries = scanner.ScanDrive(rootWithSlash, driveIndex, excludePatterns, ct, progress);
                _index.AddEntries(driveIndex, entries);

                // Save to cache
                _networkCache.Save(cacheKey, entries);

                RaiseProgress(
                    $"{rootPath} 完了 ({entries.Count:N0} 件)",
                    _index.TotalCount,
                    IndexingState.Indexing);
            }
            catch (Exception ex)
            {
                RaiseProgress(
                    $"{rootPath} エラー: {ex.Message}",
                    _index.TotalCount,
                    IndexingState.Indexing);
            }
        }

        private void RescanCustomFolder(string rootPath, string cacheKey, byte driveIndex,
            IReadOnlyList<string> excludePatterns, CancellationToken ct)
        {
            var scanner = new NetworkDriveScanner();

            try
            {
                string rootWithSlash = rootPath.EndsWith("\\") ? rootPath : rootPath + "\\";
                var entries = scanner.ScanDrive(rootWithSlash, driveIndex, excludePatterns, ct);

                _index.RemoveEntriesForDrive(driveIndex);
                _index.AddEntries(driveIndex, entries);

                _networkCache.Save(cacheKey, entries);

                RaiseProgress(
                    $"{rootPath} リスキャン完了 ({entries.Count:N0} 件)",
                    _index.TotalCount,
                    IndexingState.Ready);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Custom folder rescan failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Generate a safe cache key for a custom folder path.
        /// Uses "CF_" prefix + sanitized path to avoid collision with network drive cache keys.
        /// </summary>
        private static string GetCustomFolderCacheKey(string folderPath)
        {
            // Sanitize path for use as filename: replace \ / : with _
            string safe = folderPath.Replace("\\", "_").Replace("/", "_").Replace(":", "_");
            return "CF_" + safe;
        }

        private void OnRescanTimerTick(object state)
        {
            if (_cts == null || _cts.IsCancellationRequested) return;
            if (_currentSettings == null) return;

            // Rescan network drives
            foreach (var drive in _currentSettings.NetworkDrives.Where(d => d.Enabled))
            {
                if (_networkDriveIndexMap.TryGetValue(drive.DriveLetter, out byte driveIndex))
                {
                    string rootPath = drive.DriveLetter + "\\";
                    Task.Run(() => RescanNetworkDrive(
                        rootPath, drive.DriveLetter, driveIndex,
                        _currentSettings.ExcludePatterns, _cts.Token));
                }
            }

            // Rescan custom folders
            if (_currentSettings.CustomFolders != null)
            {
                foreach (var folder in _currentSettings.CustomFolders.Where(f => f.Enabled))
                {
                    if (string.IsNullOrWhiteSpace(folder.FolderPath)) continue;
                    if (_customFolderIndexMap.TryGetValue(folder.FolderPath, out byte driveIndex))
                    {
                        string cacheKey = GetCustomFolderCacheKey(folder.FolderPath);
                        Task.Run(() => RescanCustomFolder(
                            folder.FolderPath, cacheKey, driveIndex,
                            _currentSettings.ExcludePatterns, _cts.Token));
                    }
                }
            }
        }

        private void OnDriveChanged(DriveChangeEvent evt)
        {
            // Notify UI about drive changes
            string action = evt.ChangeType == DriveChangeType.Added ? "検出" : "切断";
            RaiseProgress(
                $"ドライブ {evt.DriveLetter} が{action}されました",
                _index.TotalCount,
                State);
        }

        /// <summary>
        /// Initialize TextExtractor for content search (grep-style, on-demand file reading).
        /// </summary>
        private void InitializeTextExtractor(AppSettings settings)
        {
            try
            {
                _textExtractor = new TextExtractor
                {
                    MaxFileSizeBytes = settings.FullTextMaxFileSizeMB > 0
                        ? settings.FullTextMaxFileSizeMB * 1024 * 1024
                        : 0,
                };

                if (!IFilterNativeMethods.IsIFilterAvailable())
                {
                    System.Diagnostics.Debug.WriteLine("IFilter (query.dll) is not available. Content search will use plain text extraction only.");
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to initialize TextExtractor: {ex.Message}");
                _textExtractor = null;
            }
        }

        private void RaiseProgress(string message, int totalEntries, IndexingState state)
        {
            ProgressChanged?.Invoke(this, new IndexingProgressEventArgs
            {
                Message = message,
                TotalEntries = totalEntries,
                State = state
            });
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                _cts?.Cancel();
                _rescanTimer?.Dispose();

                foreach (var monitor in _usnMonitors)
                    monitor.Dispose();
                _usnMonitors.Clear();

                foreach (var reader in _mftReaders)
                    reader.Dispose();
                _mftReaders.Clear();

                _cts?.Dispose();
                _disposed = true;
            }
        }
    }
}
