using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
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
        // 変更監視用: ドライブごとの watchRoot（相対パス算出の起点）。
        // ネットワークドライブは "K:\\"、カスタムフォルダは実体パス(UNC等)。
        private readonly Dictionary<byte, string> _watchRootByDrive = new Dictionary<byte, string>();
        // 変更監視用: ドライブごとの擬似FRN採番カウンタ（遅延シード）。
        private readonly Dictionary<byte, ulong> _networkFrnCounter = new Dictionary<byte, ulong>();
        private readonly object _frnCounterLock = new object();
        private NetworkChangeMonitor _changeMonitor;
        private AppSettings _currentSettings;
        private Timer _rescanTimer;
        private DateTime? _nextDailyRescanTime;

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
                // Build drive letter ↔ UNC path mapping cache from non-elevated user session.
                // This must run before indexing so that folder path resolution works correctly.
                _driveDetector.BuildDriveLetterCache();

                // Set exclude patterns for search-time filtering
                // (applies to all index types: MFT, network, custom folders)
                _index.SetExcludePatterns(settings.ExcludePatterns);

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
                    _watchRootByDrive[driveIndex] = rootPath; // "K:\\"

                    networkTasks.Add(Task.Run(() =>
                        NetworkDriveDetector.RunImpersonated(() =>
                            IndexNetworkDriveWithCache(
                                rootPath, drive.DriveLetter, driveIndex,
                                settings.ExcludePatterns, _cts.Token))));
                }

                // Index custom folders (filesystem enumeration, with cache support)
                var customFolderTasks = new List<Task>();
                if (settings.CustomFolders != null)
                {
                    foreach (var folder in settings.CustomFolders.Where(f => f.Enabled))
                    {
                        if (string.IsNullOrWhiteSpace(folder.FolderPath)) continue;

                        // ドライブレター→UNCパス変換（管理者セッションではVBS非永続マッピングが見えないため）
                        string actualPath = ResolveCustomFolderPath(folder.FolderPath);

                        // Directory.Exists must also run under user impersonation
                        // because the admin session lacks NAS credentials
                        bool exists = false;
                        NetworkDriveDetector.RunImpersonated(() =>
                        {
                            exists = System.IO.Directory.Exists(actualPath);
                        });
                        if (!exists) continue;

                        byte driveIndex = _nextDriveIndex++;
                        // Use folder path as a pseudo drive letter
                        string pseudoDrive = actualPath;
                        _index.SetDriveLetter(driveIndex, pseudoDrive);
                        _customFolderIndexMap[folder.FolderPath] = driveIndex;
                        _watchRootByDrive[driveIndex] = actualPath;

                        string rootPath = actualPath;
                        customFolderTasks.Add(Task.Run(() =>
                            NetworkDriveDetector.RunImpersonated(() =>
                                IndexCustomFolderWithCache(
                                    rootPath, driveIndex,
                                    settings.ExcludePatterns, _cts.Token))));
                    }
                }

                await Task.WhenAll(localTasks.Concat(networkTasks).Concat(customFolderTasks)).ConfigureAwait(false);

                State = IndexingState.Ready;
                RaiseProgress("インデックス構築完了", _index.TotalCount, IndexingState.Ready);

                // Start drive change monitoring
                _ = _driveDetector.MonitorDriveChanges(OnDriveChanged,
                    TimeSpan.FromSeconds(30), _cts.Token);

                // Start rescan scheduler (Daily / Interval / Manual)
                StartRescanScheduler(settings);

                // Initialize TextExtractor for content search (grep-style)
                InitializeTextExtractor(settings);
            }
            catch (Exception ex)
            {
                State = IndexingState.Error;
                RaiseProgress($"エラー: {ex.Message}", _index.TotalCount, IndexingState.Error);
            }
        }

        private void StartRescanScheduler(AppSettings settings)
        {
            bool hasTargets = settings.NetworkDrives.Any(d => d.Enabled)
                || (settings.CustomFolders != null && settings.CustomFolders.Any(f => f.Enabled));
            if (!hasTargets) return;

            switch (settings.RescanMode)
            {
                case RescanMode.Daily:
                    StartDailyScheduler(settings);
                    break;
                case RescanMode.Interval:
                    // Enforce minimum 180 minutes to protect file servers from excessive load
                    int intervalMinutes = Math.Max(settings.NetworkRescanIntervalMinutes, 180);
                    if (intervalMinutes > 0)
                    {
                        int intervalMs = intervalMinutes * 60 * 1000;
                        _rescanTimer = new Timer(OnRescanTimerTick, null, intervalMs, intervalMs);
                    }
                    break;
                case RescanMode.Manual:
                    // No automatic rescan
                    break;
            }
        }

        private void StartDailyScheduler(AppSettings settings)
        {
            // Check if we missed today's scheduled rescan
            CheckMissedDailyRescan(settings);

            // Calculate next execution time
            _nextDailyRescanTime = CalculateNextDailyRescan(settings.DailyRescanHour, settings.DailyRescanMinute);

            // Check every minute (precision is sufficient, CPU cost negligible)
            _rescanTimer = new Timer(OnDailyTimerTick, null,
                TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
        }

        private void CheckMissedDailyRescan(AppSettings settings)
        {
            // First run (no previous rescan ever recorded): skip.
            // The user just did a fresh index — triggering an immediate rescan would be wasteful
            // and could race with the initial scan's cache save operations.
            if (string.IsNullOrEmpty(settings.LastRescanTimestamp)) return;

            if (DateTime.TryParse(settings.LastRescanTimestamp, null,
                System.Globalization.DateTimeStyles.RoundtripKind, out DateTime lastRescan))
            {
                var todayScheduled = DateTime.Today
                    .AddHours(settings.DailyRescanHour)
                    .AddMinutes(settings.DailyRescanMinute);

                // Current time is past today's schedule, but last rescan was before it
                if (DateTime.Now > todayScheduled && lastRescan < todayScheduled)
                {
                    // Delay the missed rescan slightly to ensure all initial cache loads
                    // have completed (StartRescanScheduler is called after WhenAll, but
                    // giving an extra buffer avoids edge-case overlaps with SaveAllCaches).
                    Task.Run(async () =>
                    {
                        await Task.Delay(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
                        ExecuteRescanAll();
                    });
                }
            }
        }

        private static DateTime CalculateNextDailyRescan(int hour, int minute)
        {
            var today = DateTime.Today.AddHours(hour).AddMinutes(minute);
            return DateTime.Now < today ? today : today.AddDays(1);
        }

        private void OnDailyTimerTick(object state)
        {
            if (_cts == null || _cts.IsCancellationRequested) return;
            if (!_nextDailyRescanTime.HasValue) return;

            if (DateTime.Now >= _nextDailyRescanTime.Value)
            {
                _nextDailyRescanTime = _nextDailyRescanTime.Value.AddDays(1);
                Task.Run(() => ExecuteRescanAll());
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

                // On journal overflow, re-read the full MFT for this drive
                string dl = driveLetter;
                byte di = driveIndex;
                monitor.JournalOverflow += (s, e) =>
                {
                    Debug.WriteLine($"Journal overflow on {dl}: triggering full MFT rescan");
                    Task.Run(() =>
                    {
                        try
                        {
                            var reader = new MftReader(dl, di);
                            var rescanEntries = reader.EnumerateAllEntries(_cts.Token);
                            _index.RemoveEntriesForDrive(di);
                            _index.AddEntries(di, rescanEntries);
                            RaiseProgress(
                                $"{dl} MFT再スキャン完了 ({rescanEntries.Count:N0} 件)",
                                _index.TotalCount, IndexingState.Ready);
                        }
                        catch (Exception ex)
                        {
                            Debug.WriteLine($"MFT rescan failed for {dl}: {ex.Message}");
                        }
                    });
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

        private void IndexNetworkDriveWithCache(string rootPath, string driveLetter,
            byte driveIndex, IReadOnlyList<string> excludePatterns, CancellationToken ct)
        {
            // Check cache age before loading
            var cacheTimestamp = _networkCache.GetCacheTimestamp(driveLetter);

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

                // No immediate background rescan — rescan is managed by the scheduler
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

                // Save to cache (with retry for transient IO errors)
                SaveCacheWithRetry(driveLetter, entries);

                RaiseProgress(
                    $"{rootPath} 完了 ({entries.Count:N0} 件)",
                    _index.TotalCount,
                    IndexingState.Indexing);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"IndexNetworkDrive failed for {rootPath}: {ex}");
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

                // Update cache (with retry for transient IO errors)
                SaveCacheWithRetry(driveLetter, entries);

                RaiseProgress(
                    $"{driveLetter} リスキャン完了 ({entries.Count:N0} 件)",
                    _index.TotalCount,
                    IndexingState.Ready);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Network rescan failed for {rootPath}: {ex}");
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

            // Check cache age
            var cacheTimestamp = _networkCache.GetCacheTimestamp(cacheKey);

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

                // No immediate background rescan — rescan is managed by the scheduler
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

                // Save to cache (with retry for transient IO errors)
                SaveCacheWithRetry(cacheKey, entries);

                RaiseProgress(
                    $"{rootPath} 完了 ({entries.Count:N0} 件)",
                    _index.TotalCount,
                    IndexingState.Indexing);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"IndexCustomFolder failed for {rootPath}: {ex}");
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

                SaveCacheWithRetry(cacheKey, entries);

                RaiseProgress(
                    $"{rootPath} リスキャン完了 ({entries.Count:N0} 件)",
                    _index.TotalCount,
                    IndexingState.Ready);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Custom folder rescan failed for {rootPath}: {ex}");
            }
        }

        /// <summary>
        /// Resolve a custom folder path, converting drive letters to UNC paths if needed.
        /// Handles cases where VBS logon script mappings are invisible to the admin-elevated process.
        /// </summary>
        private static string ResolveCustomFolderPath(string folderPath)
        {
            if (string.IsNullOrEmpty(folderPath) || folderPath.Length < 2 || folderPath[1] != ':')
                return folderPath;

            string driveLetter = folderPath.Substring(0, 2);
            string uncBase = NetworkDriveDetector.GetUncPath(driveLetter);
            if (!string.IsNullOrEmpty(uncBase))
            {
                string remainder = folderPath.Length > 2 ? folderPath.Substring(2) : "";
                return (uncBase.TrimEnd('\\') + remainder).TrimEnd('\\');
            }
            return folderPath;
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

        /// <summary>
        /// 指定ネットワークドライブ用に、既存FRNと衝突しない擬似FRNを採番する。
        /// 初回呼び出し時に現在の最大FRNからシードする。
        /// </summary>
        private ulong AllocateNetworkFrn(byte driveIndex)
        {
            lock (_frnCounterLock)
            {
                if (!_networkFrnCounter.TryGetValue(driveIndex, out ulong current))
                {
                    ulong max = 0;
                    foreach (var e in _index.GetEntriesForDrive(driveIndex))
                        if (e.FileReferenceNumber > max) max = e.FileReferenceNumber;
                    current = max;
                }
                current++;
                _networkFrnCounter[driveIndex] = current;
                return current;
            }
        }

        private void OnRescanTimerTick(object state)
        {
            ExecuteRescanAll();
        }

        /// <summary>
        /// Execute a full rescan of all network drives and custom folders.
        /// Used by both the scheduler (Daily/Interval) and manual rescan.
        /// Updates LastRescanTimestamp and saves settings after completion.
        /// </summary>
        private void ExecuteRescanAll()
        {
            if (_cts == null || _cts.IsCancellationRequested) return;
            if (_currentSettings == null) return;

            RaiseProgress("リスキャン実行中...", _index.TotalCount, IndexingState.Indexing);

            var tasks = new List<Task>();

            // Rescan network drives
            foreach (var drive in _currentSettings.NetworkDrives.Where(d => d.Enabled))
            {
                if (_networkDriveIndexMap.TryGetValue(drive.DriveLetter, out byte driveIndex))
                {
                    string rootPath = drive.DriveLetter + "\\";
                    tasks.Add(Task.Run(() =>
                        NetworkDriveDetector.RunImpersonated(() =>
                            RescanNetworkDrive(
                                rootPath, drive.DriveLetter, driveIndex,
                                _currentSettings.ExcludePatterns, _cts.Token))));
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
                        tasks.Add(Task.Run(() =>
                            NetworkDriveDetector.RunImpersonated(() =>
                                RescanCustomFolder(
                                    folder.FolderPath, cacheKey, driveIndex,
                                    _currentSettings.ExcludePatterns, _cts.Token))));
                    }
                }
            }

            try
            {
                Task.WaitAll(tasks.ToArray());
            }
            catch (AggregateException)
            {
                // Individual rescan errors are already handled in RescanNetworkDrive/RescanCustomFolder
            }

            // Update timestamp and persist (read-modify-write to avoid overwriting dialog changes)
            _currentSettings.LastRescanTimestamp = DateTime.Now.ToString("o");
            try
            {
                var fresh = _settingsService.Load();
                fresh.LastRescanTimestamp = _currentSettings.LastRescanTimestamp;
                _settingsService.Save(fresh);
            }
            catch (Exception ex) { Debug.WriteLine($"Settings save failed: {ex.Message}"); }

            RaiseProgress("リスキャン完了", _index.TotalCount, IndexingState.Ready);
        }

        /// <summary>
        /// Trigger a manual rescan from the UI ("今すぐリスキャン" button).
        /// Runs asynchronously in the background.
        /// </summary>
        public void RequestManualRescan()
        {
            Task.Run(() => ExecuteRescanAll());
        }

        /// <summary>
        /// Trigger a rescan for a single custom folder.
        /// Runs asynchronously in the background.
        /// </summary>
        public void RequestRescanCustomFolder(string folderPath)
        {
            if (_cts == null || _cts.IsCancellationRequested) return;
            if (_currentSettings == null) return;
            if (string.IsNullOrWhiteSpace(folderPath)) return;
            if (!_customFolderIndexMap.TryGetValue(folderPath, out byte driveIndex)) return;

            string cacheKey = GetCustomFolderCacheKey(folderPath);
            Task.Run(() =>
            {
                RaiseProgress($"{folderPath} を更新中...", _index.TotalCount, IndexingState.Indexing);
                try
                {
                    NetworkDriveDetector.RunImpersonated(() =>
                        RescanCustomFolder(folderPath, cacheKey, driveIndex,
                            _currentSettings.ExcludePatterns, _cts.Token));
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Rescan failed for {folderPath}: {ex.Message}");
                }
                RaiseProgress("更新完了", _index.TotalCount, IndexingState.Ready);
            });
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

        /// <summary>
        /// Save all network/custom folder caches to disk.
        /// Called on app exit to ensure cache persistence (Everythingと同様の仕組み).
        /// </summary>
        public void SaveAllCaches()
        {
            try
            {
                // Save network drive caches
                if (_currentSettings?.NetworkDrives != null)
                {
                    foreach (var drive in _currentSettings.NetworkDrives.Where(d => d.Enabled))
                    {
                        if (_networkDriveIndexMap.TryGetValue(drive.DriveLetter, out byte driveIndex))
                        {
                            var entries = _index.GetEntriesForDrive(driveIndex);
                            if (entries.Count > 0)
                            {
                                SaveCacheWithRetry(drive.DriveLetter, entries);
                            }
                        }
                    }
                }

                // Save custom folder caches
                if (_currentSettings?.CustomFolders != null)
                {
                    foreach (var folder in _currentSettings.CustomFolders.Where(f => f.Enabled))
                    {
                        if (string.IsNullOrWhiteSpace(folder.FolderPath)) continue;
                        if (_customFolderIndexMap.TryGetValue(folder.FolderPath, out byte driveIndex))
                        {
                            var entries = _index.GetEntriesForDrive(driveIndex);
                            if (entries.Count > 0)
                            {
                                string cacheKey = GetCustomFolderCacheKey(folder.FolderPath);
                                SaveCacheWithRetry(cacheKey, entries);
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"SaveAllCaches failed: {ex}");
            }
        }

        /// <summary>
        /// Save cache with retry for transient IO errors (network glitches, file locks).
        /// Retries up to 3 times with exponential backoff (200ms, 400ms, 800ms).
        /// </summary>
        private void SaveCacheWithRetry(string cacheKey, List<FileEntry> entries)
        {
            const int maxRetries = 3;
            for (int attempt = 1; attempt <= maxRetries; attempt++)
            {
                try
                {
                    _networkCache.Save(cacheKey, entries);
                    return;
                }
                catch (IOException ex) when (attempt < maxRetries)
                {
                    Debug.WriteLine($"Cache save retry {attempt}/{maxRetries} for '{cacheKey}': {ex.Message}");
                    Thread.Sleep(200 * (1 << (attempt - 1))); // 200ms, 400ms
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Cache save failed for '{cacheKey}': {ex.Message}");
                    return;
                }
            }
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                _cts?.Cancel();
                // Disable timer callbacks before Dispose to prevent firing during cleanup
                _rescanTimer?.Change(Timeout.Infinite, Timeout.Infinite);
                _rescanTimer?.Dispose();

                // Save caches before disposing (Everythingのアプリ終了時保存と同様)
                SaveAllCaches();

                foreach (var monitor in _usnMonitors)
                    monitor.Dispose();
                _usnMonitors.Clear();

                foreach (var reader in _mftReaders)
                    reader.Dispose();
                _mftReaders.Clear();

                _cts?.Dispose();

                // Release the cached user impersonation token
                NetworkDriveDetector.ReleaseUserToken();

                _disposed = true;
            }
        }
    }
}
