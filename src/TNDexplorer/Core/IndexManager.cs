using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TNDexplorer.Models;
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
        private AppSettings _currentSettings;
        private Timer _rescanTimer;

        public FileIndex Index => _index;
        public SearchEngine SearchEngine => _searchEngine;
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

                await Task.WhenAll(localTasks.Concat(networkTasks)).ConfigureAwait(false);

                State = IndexingState.Ready;
                RaiseProgress("インデックス構築完了", _index.TotalCount, IndexingState.Ready);

                // Start drive change monitoring
                _ = _driveDetector.MonitorDriveChanges(OnDriveChanged,
                    TimeSpan.FromSeconds(30), _cts.Token);

                // Start periodic network rescan if configured
                if (settings.NetworkRescanIntervalMinutes > 0
                    && settings.NetworkDrives.Any(d => d.Enabled))
                {
                    int intervalMs = settings.NetworkRescanIntervalMinutes * 60 * 1000;
                    _rescanTimer = new Timer(OnRescanTimerTick, null, intervalMs, intervalMs);
                }
            }
            catch (Exception ex)
            {
                State = IndexingState.Error;
                RaiseProgress($"エラー: {ex.Message}", _index.TotalCount, IndexingState.Error);
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

        private void OnRescanTimerTick(object state)
        {
            if (_cts == null || _cts.IsCancellationRequested) return;
            if (_currentSettings == null) return;

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
