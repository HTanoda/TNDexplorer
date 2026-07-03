using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using TNDexplorer.Core;
using TNDexplorer.Models;
using TNDexplorer.Services;

namespace TNDexplorer.ViewModels
{
    public class FavoriteFilterItem : ViewModelBase
    {
        public string Name { get; set; }
        public string Extension { get; set; }
        public string FolderPath { get; set; }
        public string Keyword { get; set; }

        public string DisplayLabel
        {
            get
            {
                var parts = new List<string>();
                if (!string.IsNullOrEmpty(Extension)) parts.Add(Extension);
                if (!string.IsNullOrEmpty(FolderPath)) parts.Add(FolderPath);
                if (!string.IsNullOrEmpty(Keyword)) parts.Add(Keyword);
                string detail = parts.Count > 0 ? " (" + string.Join(", ", parts) + ")" : "";
                return Name + detail;
            }
        }
    }

    public class MainViewModel : ViewModelBase, IDisposable
    {
        private readonly IndexManager _indexManager;
        private readonly FileOperationService _fileOps;
        private readonly AppSettings _settings;
        private readonly SettingsService _settingsService;
        private readonly Dispatcher _dispatcher;

        private string _searchText = "";
        private IReadOnlyList<FileResultItem> _results = new List<FileResultItem>();
        private int _resultCount;
        private string _statusText = "準備中...";
        private string _indexStatusText = "";
        private bool _isIndexing;
        private SearchFilter _currentFilter = SearchFilter.All;
        private FileResultItem _selectedResult;
        private List<FileResultItem> _selectedResults = new List<FileResultItem>();
        private CancellationTokenSource _searchCts;
        private bool _disposed;

        // Advanced search
        private bool _isAdvancedSearchVisible;
        private bool _caseSensitive;
        private DateTime? _dateFrom;
        private DateTime? _dateTo;
        private string _minSizeText = "";
        private string _maxSizeText = "";
        private string _folderPath = "";
        private string _extensionFilter = "";
        private bool _includeSubfolders = true;

        // Preview
        private bool _isPreviewVisible;
        private string _previewFileName = "";

        // Favorites
        private FavoriteFilterItem _selectedFavorite;
        private string _newFavoriteName = "";
        private string _favoriteSaveStatus = "";

        // Display settings
        private bool _alternateRowColors;
        private bool _groupByFiscalYear;

        // Content search (grep-style)
        private bool _isContentSearch;
        private string _fullTextStatus = "";

        private DispatcherTimer _debounceTimer;
        private DispatcherTimer _favStatusTimer;
        private DispatcherTimer _autoRefreshTimer;
        // ライブ更新の再描画中はプレビュー再ロードを抑止する（同一ファイル再選択によるちらつき防止）
        private bool _suppressPreviewUpdate;

        // 結果一覧のソート状態（null=ユーザーソートなし。"Name"/"Path"/"Size"/"Modified"/"FullPath"）
        private string _sortBy;
        private bool _sortAscending = true;
        private int _sortRequestId;

        public string SearchText
        {
            get => _searchText;
            set
            {
                if (SetProperty(ref _searchText, value))
                {
                    ScheduleSearch();
                }
            }
        }

        public IReadOnlyList<FileResultItem> Results
        {
            get => _results;
            private set
            {
                SetProperty(ref _results, value);
                ResultCount = value?.Count ?? 0;
            }
        }

        public int ResultCount
        {
            get => _resultCount;
            private set => SetProperty(ref _resultCount, value);
        }

        public string StatusText
        {
            get => _statusText;
            private set => SetProperty(ref _statusText, value);
        }

        public string IndexStatusText
        {
            get => _indexStatusText;
            private set => SetProperty(ref _indexStatusText, value);
        }

        public bool IsIndexing
        {
            get => _isIndexing;
            private set => SetProperty(ref _isIndexing, value);
        }

        public SearchFilter CurrentFilter
        {
            get => _currentFilter;
            set
            {
                if (SetProperty(ref _currentFilter, value))
                    ScheduleSearch();
            }
        }

        public FileResultItem SelectedResult
        {
            get => _selectedResult;
            set
            {
                if (SetProperty(ref _selectedResult, value))
                {
                    // ライブ更新の再描画中はプレビューを触らない（同一ファイルの再ロード/ちらつき防止）
                    if (_suppressPreviewUpdate) return;

                    if (_isPreviewVisible && value != null && !value.IsDirectory)
                    {
                        PreviewFileName = value.Name;
                        PreviewFileChanged?.Invoke(value.FullPath);
                    }
                    else
                    {
                        PreviewFileName = "";
                        PreviewFileChanged?.Invoke(null);
                    }
                }
            }
        }

        /// <summary>結果一覧の複数選択項目（ビューのSelectionChangedで同期）。</summary>
        public List<FileResultItem> SelectedResults
        {
            get => _selectedResults;
            set => _selectedResults = value ?? new List<FileResultItem>();
        }

        /// <summary>結果一覧の縦スクロール位置を取得する（ビューが設定。ライブ更新時の位置保持用）。</summary>
        public Func<double> GetResultsScrollOffset { get; set; }
        /// <summary>結果一覧の縦スクロール位置を復元する（ビューが設定。ライブ更新時の位置保持用）。</summary>
        public Action<double> SetResultsScrollOffset { get; set; }

        public bool FilterAll
        {
            get => _currentFilter == SearchFilter.All;
            set { if (value) CurrentFilter = SearchFilter.All; }
        }

        public bool FilterFiles
        {
            get => _currentFilter == SearchFilter.FilesOnly;
            set { if (value) CurrentFilter = SearchFilter.FilesOnly; }
        }

        public bool FilterFolders
        {
            get => _currentFilter == SearchFilter.FoldersOnly;
            set { if (value) CurrentFilter = SearchFilter.FoldersOnly; }
        }

        // Advanced search properties
        public bool IsAdvancedSearchVisible
        {
            get => _isAdvancedSearchVisible;
            set => SetProperty(ref _isAdvancedSearchVisible, value);
        }

        public bool CaseSensitive
        {
            get => _caseSensitive;
            set
            {
                if (SetProperty(ref _caseSensitive, value))
                    ScheduleSearch();
            }
        }

        public DateTime? DateFrom
        {
            get => _dateFrom;
            set
            {
                if (SetProperty(ref _dateFrom, value))
                    ScheduleSearch();
            }
        }

        public DateTime? DateTo
        {
            get => _dateTo;
            set
            {
                if (SetProperty(ref _dateTo, value))
                    ScheduleSearch();
            }
        }

        public string MinSizeText
        {
            get => _minSizeText;
            set
            {
                if (SetProperty(ref _minSizeText, value))
                    ScheduleSearch();
            }
        }

        public string MaxSizeText
        {
            get => _maxSizeText;
            set
            {
                if (SetProperty(ref _maxSizeText, value))
                    ScheduleSearch();
            }
        }

        public string FolderPath
        {
            get => _folderPath;
            set
            {
                if (SetProperty(ref _folderPath, value))
                    ScheduleSearch();
            }
        }

        public string ExtensionFilter
        {
            get => _extensionFilter;
            set
            {
                if (SetProperty(ref _extensionFilter, value))
                    ScheduleSearch();
            }
        }

        public bool IncludeSubfolders
        {
            get => _includeSubfolders;
            set
            {
                if (SetProperty(ref _includeSubfolders, value))
                    ScheduleSearch();
            }
        }

        // Preview
        public bool IsPreviewVisible
        {
            get => _isPreviewVisible;
            set
            {
                if (SetProperty(ref _isPreviewVisible, value))
                {
                    PreviewVisibilityChanged?.Invoke();
                }
            }
        }

        public string PreviewFileName
        {
            get => _previewFileName;
            private set => SetProperty(ref _previewFileName, value);
        }

        public event Action PreviewVisibilityChanged;
        public event Action<string> PreviewFileChanged;
        public event Action FileOpening;

        // Search history (MRU)
        public ObservableCollection<string> SearchHistory { get; }
            = new ObservableCollection<string>();

        // Favorites
        public ObservableCollection<FavoriteFilterItem> FavoriteFilters { get; }
            = new ObservableCollection<FavoriteFilterItem>();

        public FavoriteFilterItem SelectedFavorite
        {
            get => _selectedFavorite;
            set
            {
                if (SetProperty(ref _selectedFavorite, value) && value != null)
                    ApplyFavorite(value);
            }
        }

        public string NewFavoriteName
        {
            get => _newFavoriteName;
            set => SetProperty(ref _newFavoriteName, value);
        }

        public string FavoriteSaveStatus
        {
            get => _favoriteSaveStatus;
            private set => SetProperty(ref _favoriteSaveStatus, value);
        }

        // Display settings
        public bool AlternateRowColors
        {
            get => _alternateRowColors;
            set => SetProperty(ref _alternateRowColors, value);
        }

        public bool GroupByFiscalYear
        {
            get => _groupByFiscalYear;
            set
            {
                if (SetProperty(ref _groupByFiscalYear, value))
                    ReapplyOrdering();
            }
        }

        /// <summary>結果一覧のソート列。null=ユーザーソートなし。"Name"/"Path"/"Size"/"Modified"/"FullPath"。</summary>
        public string SortBy
        {
            get => _sortBy;
            private set => SetProperty(ref _sortBy, value);
        }

        /// <summary>ソート方向。true=昇順、false=降順。</summary>
        public bool SortAscending
        {
            get => _sortAscending;
            private set => SetProperty(ref _sortAscending, value);
        }

        // Content search (grep-style)
        public bool IsContentSearch
        {
            get => _isContentSearch;
            set
            {
                if (SetProperty(ref _isContentSearch, value))
                {
                    // Auto-open advanced panel when switching to content search
                    if (value && !IsAdvancedSearchVisible)
                        IsAdvancedSearchVisible = true;
                    ScheduleSearch();
                }
            }
        }

        public string FullTextStatus
        {
            get => _fullTextStatus;
            private set => SetProperty(ref _fullTextStatus, value);
        }

        // Commands
        public RelayCommand OpenFileCommand { get; }
        public RelayCommand OpenFolderCommand { get; }
        public RelayCommand CopyPathCommand { get; }
        public RelayCommand CopyFileCommand { get; }
        public RelayCommand ShowPropertiesCommand { get; }
        public RelayCommand OpenSettingsCommand { get; }
        public RelayCommand ClearSearchCommand { get; }
        public RelayCommand ToggleAdvancedSearchCommand { get; }
        public RelayCommand ClearAdvancedSearchCommand { get; }
        public RelayCommand AddFavoriteCommand { get; }
        public RelayCommand RemoveFavoriteCommand { get; }
        public RelayCommand BrowseFolderCommand { get; }
        public RelayCommand OpenHelpCommand { get; }
        public RelayCommand OpenAboutCommand { get; }
        public RelayCommand ExportCsvCommand { get; }
        public RelayCommand CommitSearchHistoryCommand { get; }
        public RelayCommand ToggleThemeCommand { get; }

        private bool _isDarkTheme;
        public bool IsDarkTheme
        {
            get => _isDarkTheme;
            set => SetProperty(ref _isDarkTheme, value);
        }

        public event Action RequestOpenSettings;
        public event Action RequestOpenHelp;
        public event Action RequestOpenAbout;

        public MainViewModel(IndexManager indexManager, AppSettings settings, SettingsService settingsService)
        {
            _indexManager = indexManager;
            _fileOps = new FileOperationService();
            _settings = settings;
            _settingsService = settingsService;
            _dispatcher = Application.Current.Dispatcher;

            OpenFileCommand = new RelayCommand(ExecuteOpenFile, () => HasSelection());
            OpenFolderCommand = new RelayCommand(ExecuteOpenFolder, () => SelectedResult != null);
            CopyPathCommand = new RelayCommand(ExecuteCopyPath, () => HasSelection());
            CopyFileCommand = new RelayCommand(ExecuteCopyFile, () => HasSelection());
            ShowPropertiesCommand = new RelayCommand(ExecuteShowProperties, () => SelectedResult != null);
            OpenSettingsCommand = new RelayCommand(() => RequestOpenSettings?.Invoke());
            ClearSearchCommand = new RelayCommand(() => SearchText = "");
            ToggleAdvancedSearchCommand = new RelayCommand(() => IsAdvancedSearchVisible = !IsAdvancedSearchVisible);
            ClearAdvancedSearchCommand = new RelayCommand(ClearAdvancedSearch);
            AddFavoriteCommand = new RelayCommand(AddFavorite);
            RemoveFavoriteCommand = new RelayCommand(RemoveFavorite, () => SelectedFavorite != null);
            BrowseFolderCommand = new RelayCommand(BrowseFolder);
            OpenHelpCommand = new RelayCommand(() => RequestOpenHelp?.Invoke());
            OpenAboutCommand = new RelayCommand(() => RequestOpenAbout?.Invoke());
            ExportCsvCommand = new RelayCommand(ExecuteExportCsv, () => ResultCount > 0);
            CommitSearchHistoryCommand = new RelayCommand(CommitSearchHistory);
            ToggleThemeCommand = new RelayCommand(ToggleTheme);

            _isDarkTheme = string.Equals(
                ThemeManager.Normalize(settings.Theme), ThemeManager.Dark, StringComparison.Ordinal);

            _indexManager.ProgressChanged += OnIndexingProgress;

            _debounceTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(50)
            };
            _debounceTimer.Tick += (s, e) =>
            {
                _debounceTimer.Stop();
                _ = ExecuteSearchAsync();
            };

            _autoRefreshTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(800)
            };
            _autoRefreshTimer.Tick += (s, e) =>
            {
                _autoRefreshTimer.Stop();
                // 内容検索は自動更新しない。アクティブなクエリがあるときだけ再検索。
                if (_isContentSearch) return;
                if (string.IsNullOrWhiteSpace(_searchText) && !HasAdvancedFilters()) return;
                _ = ExecuteSearchAsync(preserveView: true);
            };

            _favStatusTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(2)
            };
            _favStatusTimer.Tick += (s, e) =>
            {
                _favStatusTimer.Stop();
                FavoriteSaveStatus = "";
            };

            // Load favorite filters
            LoadFavorites();

            // Load search history
            LoadSearchHistory();

            // Load display settings
            AlternateRowColors = settings.AlternateRowColors;
            GroupByFiscalYear = settings.GroupByFiscalYear;
        }

        private void LoadFavorites()
        {
            FavoriteFilters.Clear();
            if (_settings.FavoriteFilters != null)
            {
                foreach (var f in _settings.FavoriteFilters)
                {
                    FavoriteFilters.Add(new FavoriteFilterItem
                    {
                        Name = f.Name,
                        Extension = f.Extension,
                        FolderPath = f.FolderPath,
                        Keyword = f.Keyword,
                    });
                }
            }
        }

        private void SaveFavorites()
        {
            var favorites = FavoriteFilters.Select(f => new FavoriteFilter
            {
                Name = f.Name,
                Extension = f.Extension,
                FolderPath = f.FolderPath,
                Keyword = f.Keyword,
            }).ToList();
            _settings.FavoriteFilters = favorites;
            // ファイルから最新設定を読み込み、FavoriteFilters だけ更新して保存
            // （古い _settings 全体を書き戻さない）
            var fresh = _settingsService.Load();
            fresh.FavoriteFilters = favorites;
            _settingsService.Save(fresh);
        }

        private const int MaxSearchHistory = 20;

        private void LoadSearchHistory()
        {
            SearchHistory.Clear();
            if (_settings.SearchHistory != null)
            {
                foreach (var h in _settings.SearchHistory)
                {
                    if (SearchHistory.Count >= MaxSearchHistory) break;
                    if (!string.IsNullOrWhiteSpace(h))
                        SearchHistory.Add(h);
                }
            }
        }

        private void CommitSearchHistory()
        {
            if (!_settings.SearchHistoryEnabled) return;

            string term = _searchText;
            if (string.IsNullOrWhiteSpace(term)) return;

            int existing = -1;
            for (int i = 0; i < SearchHistory.Count; i++)
            {
                if (string.Equals(SearchHistory[i], term, StringComparison.Ordinal))
                {
                    existing = i;
                    break;
                }
            }
            if (existing == 0) return; // already newest

            if (existing > 0)
                SearchHistory.Move(existing, 0);
            else
                SearchHistory.Insert(0, term);

            while (SearchHistory.Count > MaxSearchHistory)
                SearchHistory.RemoveAt(SearchHistory.Count - 1);

            var history = SearchHistory.ToList();
            _settings.SearchHistory = history;
            // ファイルから最新設定を読み込み、SearchHistory だけ更新して保存
            var fresh = _settingsService.Load();
            fresh.SearchHistory = history;
            _settingsService.Save(fresh);
        }

        public void ClearSearchHistoryFromSettings()
        {
            SearchHistory.Clear();
            _settings.SearchHistory = new List<string>();
        }

        private void ToggleTheme()
        {
            string newTheme = IsDarkTheme ? ThemeManager.Light : ThemeManager.Dark;
            ThemeManager.Apply(newTheme);
            IsDarkTheme = string.Equals(newTheme, ThemeManager.Dark, StringComparison.Ordinal);

            // read-modify-write: ファイルから最新設定を読み込み、Theme だけ更新して保存
            _settings.Theme = newTheme;
            var fresh = _settingsService.Load();
            fresh.Theme = newTheme;
            _settingsService.Save(fresh);
        }

        private bool HasSelection()
        {
            return SelectedResult != null || (_selectedResults != null && _selectedResults.Count > 0);
        }

        private void AddFavorite()
        {
            string name = NewFavoriteName;
            if (string.IsNullOrWhiteSpace(name))
            {
                // Auto-generate name
                var parts = new List<string>();
                if (!string.IsNullOrEmpty(_extensionFilter)) parts.Add(_extensionFilter);
                if (!string.IsNullOrEmpty(_folderPath)) parts.Add(System.IO.Path.GetFileName(_folderPath));
                if (!string.IsNullOrEmpty(_searchText)) parts.Add(_searchText);
                name = parts.Count > 0 ? string.Join(" + ", parts) : "フィルタ " + (FavoriteFilters.Count + 1);
            }

            var item = new FavoriteFilterItem
            {
                Name = name,
                Extension = _extensionFilter,
                FolderPath = _folderPath,
                Keyword = _searchText,
            };
            FavoriteFilters.Add(item);
            NewFavoriteName = "";
            SaveFavorites();
            ShowFavoriteFeedback($"「{name}」を保存しました");
        }

        private void ShowFavoriteFeedback(string message)
        {
            FavoriteSaveStatus = message;
            _favStatusTimer.Stop();
            _favStatusTimer.Start();
        }

        private void RemoveFavorite()
        {
            if (SelectedFavorite == null) return;
            FavoriteFilters.Remove(SelectedFavorite);
            SelectedFavorite = null;
            SaveFavorites();
        }

        private void ApplyFavorite(FavoriteFilterItem fav)
        {
            if (!string.IsNullOrEmpty(fav.Extension))
                ExtensionFilter = fav.Extension;
            if (!string.IsNullOrEmpty(fav.FolderPath))
                FolderPath = fav.FolderPath;
            if (!string.IsNullOrEmpty(fav.Keyword))
                SearchText = fav.Keyword;

            if (!IsAdvancedSearchVisible && (!string.IsNullOrEmpty(fav.Extension) || !string.IsNullOrEmpty(fav.FolderPath)))
                IsAdvancedSearchVisible = true;
        }

        private void ClearAdvancedSearch()
        {
            CaseSensitive = false;
            DateFrom = null;
            DateTo = null;
            MinSizeText = "";
            MaxSizeText = "";
            FolderPath = "";
            ExtensionFilter = "";
            SelectedFavorite = null;
        }

        private void BrowseFolder()
        {
            var dialog = new Views.FolderBrowseDialog
            {
                Owner = System.Windows.Application.Current.MainWindow,
                Description = "検索対象フォルダを選択",
                SelectedPath = FolderPath ?? ""
            };
            if (dialog.ShowDialog() == true)
            {
                FolderPath = dialog.SelectedPath;
            }
        }

        public async Task StartIndexingAsync()
        {
            IsIndexing = true;
            StatusText = "インデックスを構築中...";

            await _indexManager.InitializeAsync(_settings).ConfigureAwait(false);

            _dispatcher.Invoke(() =>
            {
                IsIndexing = _indexManager.State == IndexingState.Indexing;
                UpdateStatusText();
            });
        }

        private void ScheduleSearch()
        {
            _debounceTimer.Stop();
            _debounceTimer.Start();
        }

        private AdvancedSearchOptions BuildAdvancedOptions()
        {
            // Always build options to pass hidden/system exclusion settings
            var options = new AdvancedSearchOptions
            {
                CaseSensitive = _caseSensitive,
                DateFrom = _dateFrom,
                DateTo = _dateTo,
                FolderPath = string.IsNullOrWhiteSpace(_folderPath) ? null : _folderPath,
                ExtensionFilter = string.IsNullOrWhiteSpace(_extensionFilter) ? null : _extensionFilter,
                IncludeSubfolders = _includeSubfolders,
                ExcludeHiddenFiles = _settings.ExcludeHiddenFiles,
                ExcludeSystemFiles = _settings.ExcludeSystemFiles,
            };

            if (long.TryParse(_minSizeText, out long minSize))
                options.MinSize = minSize * 1024; // KB to bytes
            if (long.TryParse(_maxSizeText, out long maxSize))
                options.MaxSize = maxSize * 1024; // KB to bytes

            return options;
        }

        private bool HasAdvancedFilters()
        {
            return _caseSensitive
                || _dateFrom.HasValue || _dateTo.HasValue
                || !string.IsNullOrWhiteSpace(_minSizeText) || !string.IsNullOrWhiteSpace(_maxSizeText)
                || !string.IsNullOrWhiteSpace(_folderPath) || !string.IsNullOrWhiteSpace(_extensionFilter);
        }

        private async Task ExecuteSearchAsync(bool preserveView = false)
        {
            string query = _searchText;

            // 年度グループ化ONの新規検索（ライブ更新以外）は既定の更新日時降順に戻す
            if (!preserveView && _groupByFiscalYear && _sortBy != null)
                SortBy = null;

            if (string.IsNullOrWhiteSpace(query) && !HasAdvancedFilters())
            {
                Results = new List<FileResultItem>();
                UpdateStatusText();
                return;
            }

            _searchCts?.Cancel();
            _searchCts = new CancellationTokenSource();
            var ct = _searchCts.Token;

            try
            {
                var advOptions = BuildAdvancedOptions();
                List<FileResultItem> results;

                // Content search mode (grep-style)
                if (_isContentSearch)
                {
                    if (string.IsNullOrWhiteSpace(query))
                    {
                        _dispatcher.Invoke(() =>
                        {
                            Results = new List<FileResultItem>();
                            StatusText = "内容検索: キーワードを入力してください";
                        });
                        return;
                    }

                    // Folder path is required for content search
                    if (string.IsNullOrWhiteSpace(_folderPath))
                    {
                        _dispatcher.Invoke(() =>
                        {
                            Results = new List<FileResultItem>();
                            if (!IsAdvancedSearchVisible)
                                IsAdvancedSearchVisible = true;
                            StatusText = "内容検索: フォルダを指定してください";
                            System.Windows.MessageBox.Show(
                                "内容検索にはフォルダの指定が必要です。\n\n" +
                                "詳細パネルの「フォルダ」欄で検索対象フォルダを指定してください。",
                                "内容検索",
                                System.Windows.MessageBoxButton.OK,
                                System.Windows.MessageBoxImage.Information);
                        });
                        return;
                    }

                    // Warn if the target folder is a network location (UNC or mapped network drive)
                    bool isNetworkFolder = false;
                    string trimmedFolder = _folderPath.TrimEnd('\\', '/');
                    if (trimmedFolder.StartsWith("\\\\"))
                    {
                        isNetworkFolder = true;
                    }
                    else if (trimmedFolder.Length >= 2 && trimmedFolder[1] == ':')
                    {
                        string drv = trimmedFolder.Substring(0, 2);
                        try
                        {
                            var driveInfo = new System.IO.DriveInfo(drv);
                            if (driveInfo.DriveType == System.IO.DriveType.Network)
                                isNetworkFolder = true;
                        }
                        catch { }
                        if (!isNetworkFolder)
                        {
                            // Also check via WNetGetConnection (mapped drives may not report as Network via DriveInfo)
                            string unc = Core.NetworkDriveDetector.GetUncPath(drv);
                            if (!string.IsNullOrEmpty(unc))
                                isNetworkFolder = true;
                        }
                    }

                    if (isNetworkFolder)
                    {
                        bool proceed = false;
                        _dispatcher.Invoke(() =>
                        {
                            var result = System.Windows.MessageBox.Show(
                                "内容検索の対象フォルダがネットワーク上のフォルダです。\n\n" +
                                "内容検索はファイルの中身を直接読み取るため、ネットワーク越しの\n" +
                                "検索はファイルサーバーに大きな負荷がかかる場合があります。\n\n" +
                                "検索対象のフォルダ範囲はできるだけ絞って指定してください。\n\n" +
                                "このまま内容検索を実行しますか？",
                                "内容検索 - ネットワークフォルダの注意",
                                System.Windows.MessageBoxButton.YesNo,
                                System.Windows.MessageBoxImage.Warning);
                            proceed = (result == System.Windows.MessageBoxResult.Yes);
                        });
                        if (!proceed)
                        {
                            _dispatcher.Invoke(() =>
                            {
                                IsContentSearch = false;
                                StatusText = "内容検索: キャンセルされました";
                            });
                            return;
                        }
                    }

                    _dispatcher.Invoke(() =>
                    {
                        StatusText = "内容検索中...";
                        FullTextStatus = "";
                    });

                    var progress = new Progress<ContentSearchProgress>(p =>
                    {
                        _dispatcher.BeginInvoke(new Action(() =>
                        {
                            if (p.IsCompleted)
                                FullTextStatus = $"内容検索完了: {p.TotalFiles:N0} 件スキャン、{p.MatchedFiles:N0} 件ヒット"
                                    + (p.FailedFiles > 0 ? $"（読み取り失敗 {p.FailedFiles:N0} 件）" : "");
                            else
                                FullTextStatus = $"内容検索: {p.ScannedFiles:N0}/{p.TotalFiles:N0} 件スキャン中... ({p.MatchedFiles:N0} 件ヒット)";
                        }));
                    });

                    results = await _indexManager.SearchEngine.ContentSearchAsync(
                        query, _currentFilter, _settings.MaxSearchResults, ct,
                        _indexManager.TextExtractor, advOptions, progress,
                        _settings.FullTextTargetExtensions)
                        .ConfigureAwait(false);

                    // Optionally also search by filename and merge results
                    if (_settings.ContentSearchIncludeFilename && !ct.IsCancellationRequested)
                    {
                        var filenameResults = await _indexManager.SearchEngine.SearchAsync(
                            query, _currentFilter, _settings.MaxSearchResults, ct, advOptions)
                            .ConfigureAwait(false);

                        // Merge: add filename results that aren't already in content results
                        if (filenameResults.Count > 0)
                        {
                            var existingPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                            foreach (var r in results)
                                existingPaths.Add(r.FullPath);

                            foreach (var r in filenameResults)
                            {
                                if (!existingPaths.Contains(r.FullPath) && results.Count < _settings.MaxSearchResults)
                                    results.Add(r);
                            }
                        }
                    }

                    // Update final status with merged result count (content + filename matches)
                    _dispatcher.BeginInvoke(new Action(() =>
                    {
                        FullTextStatus = $"内容検索完了: {results.Count:N0} 件ヒット";
                    }));
                }
                else
                {
                    // Filename search mode (default)
                    StatusText = "検索中...";

                    results = await _indexManager.SearchEngine.SearchAsync(
                        query, _currentFilter, _settings.MaxSearchResults, ct, advOptions)
                        .ConfigureAwait(false);
                }

                if (!ct.IsCancellationRequested)
                {
                    // ワーカースレッド上で並び順の決定＋年度境界マークを行ってから
                    // dispatcher へ渡す（UIスレッドでのソートを避けカクつきを防止）。
                    ApplyOrdering(results);

                    // ライブ更新時は選択行(FullPath)と縦スクロール位置を退避して復元する
                    string preservedSelection = null;
                    double preservedOffset = 0;
                    if (preserveView)
                    {
                        _dispatcher.Invoke(() =>
                        {
                            preservedSelection = SelectedResult?.FullPath;
                            preservedOffset = GetResultsScrollOffset?.Invoke() ?? 0;
                        });
                    }

                    var finalResults = results;
                    _dispatcher.Invoke(() =>
                    {
                        if (preserveView) _suppressPreviewUpdate = true;
                        try
                        {
                            Results = finalResults;

                            if (preserveView)
                            {
                                if (!string.IsNullOrEmpty(preservedSelection))
                                {
                                    FileResultItem match = null;
                                    for (int i = 0; i < finalResults.Count; i++)
                                    {
                                        if (string.Equals(finalResults[i].FullPath, preservedSelection, StringComparison.OrdinalIgnoreCase))
                                        { match = finalResults[i]; break; }
                                    }
                                    if (match != null) SelectedResult = match;
                                }
                                // レイアウト確定後にスクロール位置を復元
                                _dispatcher.BeginInvoke(
                                    new Action(() => SetResultsScrollOffset?.Invoke(preservedOffset)),
                                    System.Windows.Threading.DispatcherPriority.Background);
                            }

                            UpdateStatusText();
                        }
                        finally
                        {
                            if (preserveView) _suppressPreviewUpdate = false;
                        }
                    });
                }
            }
            catch (OperationCanceledException) { }
        }

        private void OnIndexingProgress(object sender, IndexingProgressEventArgs e)
        {
            _dispatcher.BeginInvoke(new Action(() =>
            {
                if (!string.IsNullOrEmpty(e.Message))
                    IndexStatusText = e.Message;

                IsIndexing = e.State == IndexingState.Indexing;
                UpdateStatusText();
                // インデックス変更を表示中の検索結果へライブ反映（内容検索とアイドル時は除外）
                if (!_isContentSearch && (!string.IsNullOrWhiteSpace(_searchText) || HasAdvancedFilters()))
                {
                    _autoRefreshTimer.Stop();
                    _autoRefreshTimer.Start();
                }
            }));
        }

        /// <summary>
        /// 結果一覧に列名でユーザーソートを適用する（ワーカースレッドで実行）。
        /// 現在の Results のコピーをソート＋境界マークして差し替える。連打時は
        /// 最新要求だけが反映されるよう要求IDで判定する。
        /// </summary>
        public void SetSort(string sortBy, bool ascending)
        {
            SortBy = string.IsNullOrEmpty(sortBy) ? null : sortBy;
            SortAscending = ascending;

            int requestId = Interlocked.Increment(ref _sortRequestId);
            var snapshot = new List<FileResultItem>(_results);

            Task.Run(() =>
            {
                ApplyOrdering(snapshot);
                _dispatcher.BeginInvoke(new Action(() =>
                {
                    // 連打時は最新要求のみ反映（古い結果は破棄）
                    if (requestId != Volatile.Read(ref _sortRequestId)) return;

                    string preservedSelection = SelectedResult?.FullPath;
                    double preservedOffset = GetResultsScrollOffset?.Invoke() ?? 0;

                    _suppressPreviewUpdate = true;
                    try
                    {
                        Results = snapshot;

                        if (!string.IsNullOrEmpty(preservedSelection))
                        {
                            FileResultItem match = null;
                            for (int i = 0; i < snapshot.Count; i++)
                            {
                                if (string.Equals(snapshot[i].FullPath, preservedSelection, StringComparison.OrdinalIgnoreCase))
                                { match = snapshot[i]; break; }
                            }
                            if (match != null) SelectedResult = match;
                        }
                        _dispatcher.BeginInvoke(
                            new Action(() => SetResultsScrollOffset?.Invoke(preservedOffset)),
                            DispatcherPriority.Background);
                    }
                    finally
                    {
                        _suppressPreviewUpdate = false;
                    }
                }));
            });
        }

        /// <summary>
        /// 設定変更（GroupByFiscalYear 等）で並び順の前提が変わった際に、
        /// 現在の Results へ決定ルールを再適用する。ユーザーソート状態は維持する。
        /// </summary>
        private void ReapplyOrdering()
        {
            if (_results == null || _results.Count == 0) return;
            SetSort(_sortBy, _sortAscending);
        }

        /// <summary>
        /// 並び順の決定ルールを1箇所に集約:
        ///   SortBy が非null → ユーザーソート。
        ///   SortBy が null かつ GroupByFiscalYear → Modified 降順（従来挙動）。
        ///   どちらでもなければ SearchEngine が返した順（FullPath昇順）のまま。
        /// 最終的な並び順が「Modified 順（昇順/降順）」の場合のみ年度境界をマークし、
        /// それ以外は境界情報をクリアする（表示の不整合を解消）。
        /// </summary>
        private void ApplyOrdering(List<FileResultItem> items)
        {
            if (items == null || items.Count == 0) return;

            bool isModifiedOrder;
            if (!string.IsNullOrEmpty(_sortBy))
            {
                items.Sort(GetComparison(_sortBy, _sortAscending));
                isModifiedOrder = string.Equals(_sortBy, "Modified", StringComparison.Ordinal);
            }
            else if (_groupByFiscalYear)
            {
                items.Sort(GetComparison("Modified", false));
                isModifiedOrder = true;
            }
            else
            {
                // SearchEngine が返した順（FullPath昇順）のまま
                isModifiedOrder = false;
            }

            if (isModifiedOrder)
                MarkFiscalYearBoundaries(items);
            else
                ClearFiscalYearBoundaries(items);
        }

        /// <summary>
        /// 列名に対応する比較ロジックを返す。二次キーは常に FullPath 昇順で決定的順序を維持する。
        /// 文字列列は CollectionView の従来挙動に合わせカルチャ依存の String.Compare を用いる。
        /// </summary>
        private static Comparison<FileResultItem> GetComparison(string sortBy, bool ascending)
        {
            int sign = ascending ? 1 : -1;
            switch (sortBy)
            {
                case "Size":
                    return (a, b) =>
                    {
                        int c = a.Size.CompareTo(b.Size);
                        if (c != 0) return sign * c;
                        return CompareFullPath(a, b);
                    };
                case "Modified":
                    return (a, b) =>
                    {
                        int c = a.Modified.CompareTo(b.Modified);
                        if (c != 0) return sign * c;
                        return CompareFullPath(a, b);
                    };
                case "Path":
                    return (a, b) =>
                    {
                        int c = string.Compare(a.Path, b.Path);
                        if (c != 0) return sign * c;
                        return CompareFullPath(a, b);
                    };
                case "FullPath":
                    return (a, b) => sign * string.Compare(a.FullPath, b.FullPath);
                case "Name":
                default:
                    return (a, b) =>
                    {
                        int c = string.Compare(a.Name, b.Name);
                        if (c != 0) return sign * c;
                        return CompareFullPath(a, b);
                    };
            }
        }

        private static int CompareFullPath(FileResultItem a, FileResultItem b)
        {
            return string.Compare(a.FullPath, b.FullPath, StringComparison.OrdinalIgnoreCase);
        }

        private static void ClearFiscalYearBoundaries(IReadOnlyList<FileResultItem> items)
        {
            for (int i = 0; i < items.Count; i++)
            {
                items[i].IsFiscalYearBoundary = false;
                items[i].FiscalYearLabel = null;
            }
        }

        private void MarkFiscalYearBoundaries(IReadOnlyList<FileResultItem> items)
        {
            if (items.Count == 0) return;

            // First item always gets a label
            int prevFY = items[0].FiscalYear;
            items[0].IsFiscalYearBoundary = true;
            items[0].FiscalYearLabel = $"{prevFY}年度";

            for (int i = 1; i < items.Count; i++)
            {
                int fy = items[i].FiscalYear;
                if (fy != prevFY)
                {
                    items[i].IsFiscalYearBoundary = true;
                    items[i].FiscalYearLabel = $"{fy}年度";
                    prevFY = fy;
                }
                else
                {
                    items[i].IsFiscalYearBoundary = false;
                    items[i].FiscalYearLabel = null;
                }
            }
        }

        private void UpdateStatusText()
        {
            int total = _indexManager.Index.TotalCount;
            string mode = _isContentSearch ? "[内容検索] " : "";

            string selectionSuffix = "";
            if (_selectedResults != null && _selectedResults.Count > 1)
                selectionSuffix = $" - {_selectedResults.Count:N0} 件選択";

            if (!string.IsNullOrWhiteSpace(_searchText) || HasAdvancedFilters())
            {
                StatusText = $"{mode}{ResultCount:N0} 件ヒット (全 {total:N0} 件中){selectionSuffix}";
            }
            else
            {
                StatusText = $"{total:N0} 件のファイル/フォルダをインデックス済み";
            }
        }

        /// <summary>複数選択の変更をステータスバーへ反映する（ビューのSelectionChangedから呼ぶ）。</summary>
        public void OnSelectionChanged()
        {
            UpdateStatusText();
        }

        private List<FileResultItem> GetSelectionForActions()
        {
            if (_selectedResults != null && _selectedResults.Count > 1)
                return _selectedResults;
            if (SelectedResult != null)
                return new List<FileResultItem> { SelectedResult };
            if (_selectedResults != null && _selectedResults.Count == 1)
                return new List<FileResultItem>(_selectedResults);
            return new List<FileResultItem>();
        }

        private void ExecuteOpenFile()
        {
            var selection = GetSelectionForActions();
            if (selection.Count == 0) return;

            if (selection.Count >= 6)
            {
                var confirm = System.Windows.MessageBox.Show(
                    $"{selection.Count} 件のファイルを開きます。よろしいですか？",
                    "複数ファイルを開く",
                    System.Windows.MessageBoxButton.YesNo,
                    System.Windows.MessageBoxImage.Question);
                if (confirm != System.Windows.MessageBoxResult.Yes) return;
            }

            CommitSearchHistory();
            FileOpening?.Invoke();
            foreach (var item in selection)
                _fileOps.OpenFile(item.FullPath);
        }

        private void ExecuteOpenFolder()
        {
            if (SelectedResult == null) return;
            _fileOps.OpenContainingFolder(SelectedResult.FullPath);
        }

        private void ExecuteCopyPath()
        {
            var selection = GetSelectionForActions();
            if (selection.Count == 0) return;
            _fileOps.CopyPathToClipboard(selection.Select(i => i.FullPath).ToList());
        }

        private void ExecuteCopyFile()
        {
            var selection = GetSelectionForActions();
            if (selection.Count == 0) return;
            _fileOps.CopyFileToClipboard(selection.Select(i => i.FullPath).ToList());
        }

        private void ExecuteShowProperties()
        {
            if (SelectedResult == null) return;
            _fileOps.ShowProperties(SelectedResult.FullPath);
        }

        private void ExecuteExportCsv()
        {
            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                Filter = "CSV ファイル (*.csv)|*.csv",
                DefaultExt = ".csv",
                FileName = "検索結果_" + DateTime.Now.ToString("yyyyMMdd_HHmmss")
            };
            if (dialog.ShowDialog() == true)
            {
                _fileOps.ExportResultsToCsv(_results, dialog.FileName);
            }
        }

        public void OnItemDoubleClick()
        {
            ExecuteOpenFile();
        }

        public void ReloadFavorites()
        {
            var settings = _settingsService.Load();
            _settings.FavoriteFilters = settings.FavoriteFilters;
            _settings.ExcludeHiddenFiles = settings.ExcludeHiddenFiles;
            _settings.ExcludeSystemFiles = settings.ExcludeSystemFiles;
            _settings.AlternateRowColors = settings.AlternateRowColors;
            _settings.GroupByFiscalYear = settings.GroupByFiscalYear;
            _settings.SearchHistoryEnabled = settings.SearchHistoryEnabled;
            AlternateRowColors = settings.AlternateRowColors;
            GroupByFiscalYear = settings.GroupByFiscalYear;
            LoadFavorites();

            // Update content search settings
            _settings.FullTextTargetExtensions = settings.FullTextTargetExtensions;
            _settings.FullTextMaxFileSizeMB = settings.FullTextMaxFileSizeMB;
            _settings.ContentSearchIncludeFilename = settings.ContentSearchIncludeFilename;
        }

        public AppSettings GetSettings() => _settings;

        public void SaveColumnOrder(List<string> order)
        {
            // ファイルから最新設定を読み込み、ColumnOrder だけ更新して保存
            var fresh = _settingsService.Load();
            fresh.ColumnOrder = order;
            _settingsService.Save(fresh);
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                _searchCts?.Cancel();
                _searchCts?.Dispose();
                _debounceTimer?.Stop();
                _autoRefreshTimer?.Stop();
                _favStatusTimer?.Stop();
                _disposed = true;
            }
        }
    }
}
