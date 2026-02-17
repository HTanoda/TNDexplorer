using System.Collections.ObjectModel;
using System.Linq;
using TNDexplorer.Core;
using TNDexplorer.Models;
using TNDexplorer.Services;

namespace TNDexplorer.ViewModels
{
    public class DriveSelectionItem : ViewModelBase
    {
        private bool _isSelected;

        public string DriveLetter { get; set; }
        public string DisplayName { get; set; }
        public string SizeInfo { get; set; }
        public string UncPath { get; set; }
        public bool IsNetwork { get; set; }

        public bool IsSelected
        {
            get => _isSelected;
            set => SetProperty(ref _isSelected, value);
        }
    }

    public class SetupWizardViewModel : ViewModelBase
    {
        private readonly NetworkDriveDetector _detector;
        private readonly SettingsService _settingsService;

        public ObservableCollection<DriveSelectionItem> LocalDrives { get; }
            = new ObservableCollection<DriveSelectionItem>();

        public ObservableCollection<DriveSelectionItem> NetworkDrives { get; }
            = new ObservableCollection<DriveSelectionItem>();

        public RelayCommand SaveCommand { get; }
        public RelayCommand RefreshCommand { get; }

        public bool HasNetworkDrives => NetworkDrives.Count > 0;

        public SetupWizardViewModel(SettingsService settingsService)
        {
            _detector = new NetworkDriveDetector();
            _settingsService = settingsService;

            SaveCommand = new RelayCommand(Save);
            RefreshCommand = new RelayCommand(DetectDrives);

            DetectDrives();
        }

        public void DetectDrives()
        {
            LocalDrives.Clear();
            NetworkDrives.Clear();

            var localDrives = _detector.DetectLocalNtfsDrives();
            foreach (var drive in localDrives)
            {
                LocalDrives.Add(new DriveSelectionItem
                {
                    DriveLetter = drive.DriveLetter,
                    DisplayName = drive.DisplayName,
                    SizeInfo = drive.SizeDisplay,
                    IsNetwork = false,
                    IsSelected = true, // All local drives enabled by default
                });
            }

            var networkDrives = _detector.DetectMappedDrives();
            foreach (var drive in networkDrives)
            {
                NetworkDrives.Add(new DriveSelectionItem
                {
                    DriveLetter = drive.DriveLetter,
                    DisplayName = drive.DisplayName,
                    SizeInfo = drive.SizeDisplay,
                    UncPath = drive.UncPath,
                    IsNetwork = true,
                    IsSelected = false, // Network drives unchecked by default
                });
            }

            OnPropertyChanged(nameof(HasNetworkDrives));
        }

        public AppSettings CreateSettings()
        {
            var settings = new AppSettings
            {
                IsFirstRun = false,
                LocalDrives = LocalDrives.Select(d => new DriveSettings
                {
                    DriveLetter = d.DriveLetter,
                    Enabled = d.IsSelected,
                }).ToList(),
                NetworkDrives = NetworkDrives.Select(d => new NetworkDriveSettings
                {
                    DriveLetter = d.DriveLetter,
                    UncPath = d.UncPath,
                    Enabled = d.IsSelected,
                }).ToList(),
                ExcludePatterns = new System.Collections.Generic.List<string>
                {
                    "$Recycle.Bin",
                    "System Volume Information",
                },
            };

            return settings;
        }

        private void Save()
        {
            var settings = CreateSettings();
            _settingsService.Save(settings);
        }
    }
}
