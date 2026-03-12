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

        public RelayCommand SaveCommand { get; }

        public SetupWizardViewModel(SettingsService settingsService)
        {
            _detector = new NetworkDriveDetector();
            _settingsService = settingsService;

            SaveCommand = new RelayCommand(Save);

            DetectLocalDrives();
        }

        public void DetectLocalDrives()
        {
            LocalDrives.Clear();

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
                // Network drives are no longer auto-detected; use custom folders instead
                NetworkDrives = new System.Collections.Generic.List<NetworkDriveSettings>(),
                ExcludePatterns = new System.Collections.Generic.List<string>
                {
                    "$Recycle.Bin",
                    "System Volume Information",
                    "AppData",
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
