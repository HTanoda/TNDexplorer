using System;
using System.Collections.ObjectModel;
using System.Linq;
using TNDexplorer.Core;
using TNDexplorer.Models;
using TNDexplorer.Services;

namespace TNDexplorer.ViewModels
{
    public class SettingsFavoriteItem : ViewModelBase
    {
        public string Name { get; set; }
        public string Extension { get; set; }
        public string FolderPath { get; set; }
        public string Keyword { get; set; }
    }

    public class CustomFolderItem : ViewModelBase
    {
        private bool _isSelected = true;
        private string _folderPath;

        public bool IsSelected
        {
            get => _isSelected;
            set => SetProperty(ref _isSelected, value);
        }

        public string FolderPath
        {
            get => _folderPath;
            set => SetProperty(ref _folderPath, value);
        }
    }

    public class SettingsViewModel : ViewModelBase
    {
        private static readonly string[] DefaultContentSearchExtensions = new[]
        {
            ".txt", ".csv", ".tsv", ".log", ".md", ".json", ".xml", ".html", ".htm",
            ".doc", ".xls", ".ppt", ".docx", ".xlsx", ".pptx", ".pdf", ".rtf",
        };

        private readonly SettingsService _settingsService;
        private readonly NetworkDriveDetector _detector;
        private AppSettings _settings;

        private int _maxSearchResults;
        private int _networkRescanIntervalMinutes;
        private string _excludePatternsText;
        private bool _excludeHiddenFiles;
        private bool _excludeSystemFiles;

        // Display settings
        private bool _alternateRowColors;
        private bool _groupByFiscalYear;

        // Rescan settings
        private int _rescanModeIndex;
        private int _dailyRescanHour;
        private int _dailyRescanMinute;

        // Content search settings
        private string _fullTextTargetExtensionsText;
        private long _fullTextMaxFileSizeMB;
        private bool _contentSearchIncludeFilename;

        // New favorite fields
        private string _newSettingsFavName = "";
        private string _newSettingsFavExt = "";
        private string _newSettingsFavFolder = "";
        private string _newSettingsFavKeyword = "";

        // Edit mode
        private bool _isEditingFavorite;
        private SettingsFavoriteItem _editingFavoriteItem;
        private string _favRegisterButtonText = "登録";

        public ObservableCollection<DriveSelectionItem> LocalDrives { get; }
            = new ObservableCollection<DriveSelectionItem>();

        public ObservableCollection<DriveSelectionItem> NetworkDrives { get; }
            = new ObservableCollection<DriveSelectionItem>();

        public ObservableCollection<SettingsFavoriteItem> SettingsFavorites { get; }
            = new ObservableCollection<SettingsFavoriteItem>();

        public ObservableCollection<CustomFolderItem> CustomFolders { get; }
            = new ObservableCollection<CustomFolderItem>();

        public int MaxSearchResults
        {
            get => _maxSearchResults;
            set => SetProperty(ref _maxSearchResults, value);
        }

        public int NetworkRescanIntervalMinutes
        {
            get => _networkRescanIntervalMinutes;
            set => SetProperty(ref _networkRescanIntervalMinutes, value);
        }

        public string ExcludePatternsText
        {
            get => _excludePatternsText;
            set => SetProperty(ref _excludePatternsText, value);
        }

        public bool ExcludeHiddenFiles
        {
            get => _excludeHiddenFiles;
            set => SetProperty(ref _excludeHiddenFiles, value);
        }

        public bool ExcludeSystemFiles
        {
            get => _excludeSystemFiles;
            set => SetProperty(ref _excludeSystemFiles, value);
        }

        public string NewSettingsFavName
        {
            get => _newSettingsFavName;
            set => SetProperty(ref _newSettingsFavName, value);
        }

        public string NewSettingsFavExt
        {
            get => _newSettingsFavExt;
            set => SetProperty(ref _newSettingsFavExt, value);
        }

        public string NewSettingsFavFolder
        {
            get => _newSettingsFavFolder;
            set => SetProperty(ref _newSettingsFavFolder, value);
        }

        public string NewSettingsFavKeyword
        {
            get => _newSettingsFavKeyword;
            set => SetProperty(ref _newSettingsFavKeyword, value);
        }

        public bool IsEditingFavorite
        {
            get => _isEditingFavorite;
            private set => SetProperty(ref _isEditingFavorite, value);
        }

        public string FavRegisterButtonText
        {
            get => _favRegisterButtonText;
            private set => SetProperty(ref _favRegisterButtonText, value);
        }

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

        public int RescanModeIndex
        {
            get => _rescanModeIndex;
            set => SetProperty(ref _rescanModeIndex, value);
        }

        public int DailyRescanHour
        {
            get => _dailyRescanHour;
            set => SetProperty(ref _dailyRescanHour, value);
        }

        public int DailyRescanMinute
        {
            get => _dailyRescanMinute;
            set => SetProperty(ref _dailyRescanMinute, value);
        }

        /// <summary>
        /// Callback to trigger manual rescan from the settings dialog.
        /// Set by App.xaml.cs before showing the dialog.
        /// </summary>
        public Action ManualRescanAction { get; set; }

        public string FullTextTargetExtensionsText
        {
            get => _fullTextTargetExtensionsText;
            set => SetProperty(ref _fullTextTargetExtensionsText, value);
        }

        public long FullTextMaxFileSizeMB
        {
            get => _fullTextMaxFileSizeMB;
            set => SetProperty(ref _fullTextMaxFileSizeMB, value);
        }

        public bool ContentSearchIncludeFilename
        {
            get => _contentSearchIncludeFilename;
            set => SetProperty(ref _contentSearchIncludeFilename, value);
        }

        public RelayCommand RefreshDrivesCommand { get; }
        public RelayCommand SaveCommand { get; }

        public SettingsViewModel(SettingsService settingsService)
        {
            _settingsService = settingsService;
            _detector = new NetworkDriveDetector();

            RefreshDrivesCommand = new RelayCommand(RefreshDrives);
            SaveCommand = new RelayCommand(Save);

            LoadSettings();
        }

        private void LoadSettings()
        {
            _settings = _settingsService.Load();
            MaxSearchResults = _settings.MaxSearchResults;
            NetworkRescanIntervalMinutes = _settings.NetworkRescanIntervalMinutes;
            ExcludeHiddenFiles = _settings.ExcludeHiddenFiles;
            ExcludeSystemFiles = _settings.ExcludeSystemFiles;
            ExcludePatternsText = string.Join("\n", _settings.ExcludePatterns ?? Enumerable.Empty<string>());
            AlternateRowColors = _settings.AlternateRowColors;
            GroupByFiscalYear = _settings.GroupByFiscalYear;

            // Rescan settings
            RescanModeIndex = (int)_settings.RescanMode;
            DailyRescanHour = _settings.DailyRescanHour;
            DailyRescanMinute = _settings.DailyRescanMinute;

            // Content search settings
            FullTextMaxFileSizeMB = _settings.FullTextMaxFileSizeMB;
            ContentSearchIncludeFilename = _settings.ContentSearchIncludeFilename;
            var ftsExts = _settings.FullTextTargetExtensions;
            if (ftsExts == null || ftsExts.Count == 0)
                ftsExts = new System.Collections.Generic.List<string>(DefaultContentSearchExtensions);
            FullTextTargetExtensionsText = string.Join("; ", ftsExts);

            RefreshDrives();
            LoadFavorites();
            LoadCustomFolders();
        }

        private void LoadCustomFolders()
        {
            CustomFolders.Clear();
            if (_settings.CustomFolders != null)
            {
                foreach (var f in _settings.CustomFolders)
                {
                    CustomFolders.Add(new CustomFolderItem
                    {
                        FolderPath = f.FolderPath,
                        IsSelected = f.Enabled,
                    });
                }
            }
        }

        public void AddCustomFolder(string folderPath)
        {
            if (string.IsNullOrWhiteSpace(folderPath)) return;
            // Avoid duplicates
            foreach (var existing in CustomFolders)
            {
                if (string.Equals(existing.FolderPath, folderPath, System.StringComparison.OrdinalIgnoreCase))
                    return;
            }
            CustomFolders.Add(new CustomFolderItem
            {
                FolderPath = folderPath,
                IsSelected = true,
            });
        }

        public void RemoveCustomFolders(System.Collections.Generic.IList<CustomFolderItem> items)
        {
            foreach (var item in items)
            {
                CustomFolders.Remove(item);
            }
        }

        private void LoadFavorites()
        {
            SettingsFavorites.Clear();
            if (_settings.FavoriteFilters != null)
            {
                foreach (var f in _settings.FavoriteFilters)
                {
                    SettingsFavorites.Add(new SettingsFavoriteItem
                    {
                        Name = f.Name,
                        Extension = f.Extension,
                        FolderPath = f.FolderPath,
                        Keyword = f.Keyword,
                    });
                }
            }
        }

        public void BeginEditFavorite(SettingsFavoriteItem item)
        {
            _editingFavoriteItem = item;
            IsEditingFavorite = true;
            FavRegisterButtonText = "上書き保存";

            // Populate input fields with selected item's data
            NewSettingsFavName = item.Name;
            NewSettingsFavExt = item.Extension;
            NewSettingsFavFolder = item.FolderPath;
            NewSettingsFavKeyword = item.Keyword;
        }

        public void CancelEditFavorite()
        {
            _editingFavoriteItem = null;
            IsEditingFavorite = false;
            FavRegisterButtonText = "登録";

            NewSettingsFavName = "";
            NewSettingsFavExt = "";
            NewSettingsFavFolder = "";
            NewSettingsFavKeyword = "";
        }

        public void AddOrUpdateSettingsFavorite()
        {
            if (IsEditingFavorite && _editingFavoriteItem != null)
            {
                // Update existing item
                string name = NewSettingsFavName;
                if (string.IsNullOrWhiteSpace(name))
                {
                    name = GenerateAutoName();
                }

                _editingFavoriteItem.Name = name;
                _editingFavoriteItem.Extension = _newSettingsFavExt;
                _editingFavoriteItem.FolderPath = _newSettingsFavFolder;
                _editingFavoriteItem.Keyword = _newSettingsFavKeyword;

                // Refresh the ListView by replacing the item
                int idx = SettingsFavorites.IndexOf(_editingFavoriteItem);
                if (idx >= 0)
                {
                    SettingsFavorites.RemoveAt(idx);
                    SettingsFavorites.Insert(idx, new SettingsFavoriteItem
                    {
                        Name = name,
                        Extension = _newSettingsFavExt,
                        FolderPath = _newSettingsFavFolder,
                        Keyword = _newSettingsFavKeyword,
                    });
                }

                CancelEditFavorite();
            }
            else
            {
                // Add new item
                AddSettingsFavorite();
            }
        }

        public void AddSettingsFavorite()
        {
            string name = NewSettingsFavName;
            if (string.IsNullOrWhiteSpace(name))
            {
                name = GenerateAutoName();
            }

            SettingsFavorites.Add(new SettingsFavoriteItem
            {
                Name = name,
                Extension = _newSettingsFavExt,
                FolderPath = _newSettingsFavFolder,
                Keyword = _newSettingsFavKeyword,
            });

            NewSettingsFavName = "";
            NewSettingsFavExt = "";
            NewSettingsFavFolder = "";
            NewSettingsFavKeyword = "";
        }

        private string GenerateAutoName()
        {
            var parts = new System.Collections.Generic.List<string>();
            if (!string.IsNullOrEmpty(_newSettingsFavExt)) parts.Add(_newSettingsFavExt);
            if (!string.IsNullOrEmpty(_newSettingsFavFolder))
                parts.Add(System.IO.Path.GetFileName(_newSettingsFavFolder));
            if (!string.IsNullOrEmpty(_newSettingsFavKeyword)) parts.Add(_newSettingsFavKeyword);
            return parts.Count > 0 ? string.Join(" + ", parts) : "フィルタ " + (SettingsFavorites.Count + 1);
        }

        private void RefreshDrives()
        {
            LocalDrives.Clear();

            var localDrives = _detector.DetectLocalNtfsDrives();
            foreach (var drive in localDrives)
            {
                var existing = _settings.LocalDrives?.FirstOrDefault(
                    d => d.DriveLetter == drive.DriveLetter);

                LocalDrives.Add(new DriveSelectionItem
                {
                    DriveLetter = drive.DriveLetter,
                    DisplayName = drive.DisplayName,
                    SizeInfo = drive.SizeDisplay,
                    IsNetwork = false,
                    IsSelected = existing?.Enabled ?? true,
                });
            }
        }

        private void Save()
        {
            _settings.LocalDrives = LocalDrives.Select(d => new DriveSettings
            {
                DriveLetter = d.DriveLetter,
                Enabled = d.IsSelected,
            }).ToList();

            _settings.MaxSearchResults = MaxSearchResults;
            _settings.NetworkRescanIntervalMinutes = NetworkRescanIntervalMinutes;
            _settings.ExcludeHiddenFiles = ExcludeHiddenFiles;
            _settings.ExcludeSystemFiles = ExcludeSystemFiles;

            _settings.ExcludePatterns = (ExcludePatternsText ?? "")
                .Split(new[] { '\n', '\r' }, System.StringSplitOptions.RemoveEmptyEntries)
                .Select(s => s.Trim())
                .Where(s => !string.IsNullOrEmpty(s))
                .ToList();

            // Save favorites from the settings tab
            _settings.FavoriteFilters = SettingsFavorites.Select(f => new FavoriteFilter
            {
                Name = f.Name,
                Extension = f.Extension,
                FolderPath = f.FolderPath,
                Keyword = f.Keyword,
            }).ToList();

            // Save custom folders
            _settings.CustomFolders = CustomFolders.Select(f => new CustomFolderSettings
            {
                FolderPath = f.FolderPath,
                Enabled = f.IsSelected,
            }).ToList();

            // Save display settings
            _settings.AlternateRowColors = AlternateRowColors;
            _settings.GroupByFiscalYear = GroupByFiscalYear;

            // Save rescan settings
            _settings.RescanMode = (RescanMode)RescanModeIndex;
            _settings.DailyRescanHour = DailyRescanHour;
            _settings.DailyRescanMinute = DailyRescanMinute;

            // Save content search settings
            _settings.FullTextMaxFileSizeMB = FullTextMaxFileSizeMB;
            _settings.ContentSearchIncludeFilename = ContentSearchIncludeFilename;
            _settings.FullTextTargetExtensions = (FullTextTargetExtensionsText ?? "")
                .Split(new[] { ';', ',', '\n', '\r' }, System.StringSplitOptions.RemoveEmptyEntries)
                .Select(s => s.Trim())
                .Where(s => !string.IsNullOrEmpty(s))
                .ToList();

            _settings.IsFirstRun = false;
            _settingsService.Save(_settings);
        }

        public AppSettings GetCurrentSettings()
        {
            Save();
            return _settings;
        }

    }
}
