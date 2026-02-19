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

        public string SearchText
        {
            get => _searchText;
            set
            {
                if (SetProperty(ref _searchText, value))
                    ScheduleSearch();
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
            set => SetProperty(ref _groupByFiscalYear, value);
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

            OpenFileCommand = new RelayCommand(ExecuteOpenFile, () => SelectedResult != null);
            OpenFolderCommand = new RelayCommand(ExecuteOpenFolder, () => SelectedResult != null);
            CopyPathCommand = new RelayCommand(ExecuteCopyPath, () => SelectedResult != null);
            CopyFileCommand = new RelayCommand(ExecuteCopyFile, () => SelectedResult != null);
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
            _settings.FavoriteFilters = FavoriteFilters.Select(f => new FavoriteFilter
            {
                Name = f.Name,
                Extension = f.Extension,
                FolderPath = f.FolderPath,
                Keyword = f.Keyword,
            }).ToList();
            _settingsService.Save(_settings);
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

        private async Task ExecuteSearchAsync()
        {
            string query = _searchText;

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

                    _dispatcher.Invoke(() =>
                    {
                        StatusText = "内容検索中...";
                        FullTextStatus = "";
                    });

                    var progress = new Progress<ContentSearchProgress>(p =>
                    {
                        _dispatcher.BeginInvoke(new Action(() =>
                        {
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
                    // Sort by Modified descending and mark fiscal year boundaries
                    if (_groupByFiscalYear && results.Count > 0)
                    {
                        results.Sort((a, b) => b.Modified.CompareTo(a.Modified));
                        MarkFiscalYearBoundaries(results);
                    }

                    _dispatcher.Invoke(() =>
                    {
                        Results = results;
                        UpdateStatusText();
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
            }));
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

            if (!string.IsNullOrWhiteSpace(_searchText) || HasAdvancedFilters())
            {
                StatusText = $"{mode}{ResultCount:N0} 件ヒット (全 {total:N0} 件中)";
            }
            else
            {
                StatusText = $"{total:N0} 件のファイル/フォルダをインデックス済み";
            }
        }

        private void ExecuteOpenFile()
        {
            if (SelectedResult == null) return;
            _fileOps.OpenFile(SelectedResult.FullPath);
        }

        private void ExecuteOpenFolder()
        {
            if (SelectedResult == null) return;
            _fileOps.OpenContainingFolder(SelectedResult.FullPath);
        }

        private void ExecuteCopyPath()
        {
            if (SelectedResult == null) return;
            _fileOps.CopyPathToClipboard(SelectedResult.FullPath);
        }

        private void ExecuteCopyFile()
        {
            if (SelectedResult == null) return;
            _fileOps.CopyFileToClipboard(SelectedResult.FullPath);
        }

        private void ExecuteShowProperties()
        {
            if (SelectedResult == null) return;
            _fileOps.ShowProperties(SelectedResult.FullPath);
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
            AlternateRowColors = settings.AlternateRowColors;
            GroupByFiscalYear = settings.GroupByFiscalYear;
            LoadFavorites();

            // Update content search settings
            _settings.FullTextTargetExtensions = settings.FullTextTargetExtensions;
            _settings.FullTextMaxFileSizeMB = settings.FullTextMaxFileSizeMB;
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                _searchCts?.Cancel();
                _searchCts?.Dispose();
                _debounceTimer?.Stop();
                _favStatusTimer?.Stop();
                _disposed = true;
            }
        }
    }
}
