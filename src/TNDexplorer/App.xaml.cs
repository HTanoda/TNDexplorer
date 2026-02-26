using System;
using System.Diagnostics;
using System.Security.Principal;
using System.Windows;
using TNDexplorer.Core;
using TNDexplorer.Models;
using TNDexplorer.Services;
using TNDexplorer.ViewModels;
using TNDexplorer.Views;

namespace TNDexplorer
{
    public partial class App : Application
    {
        private IndexManager _indexManager;

        private void Application_Startup(object sender, StartupEventArgs e)
        {
            // Auto-elevate to admin (UAC prompt will appear)
            if (!IsRunningAsAdmin())
            {
                RestartAsAdmin();
                Shutdown();
                return;
            }

            // Prevent shutdown when wizard dialog closes before MainWindow is assigned
            ShutdownMode = ShutdownMode.OnExplicitShutdown;

            var settingsService = new SettingsService();
            var settings = settingsService.Load();

            // First run: show setup wizard
            if (settings.IsFirstRun)
            {
                var wizardVm = new SetupWizardViewModel(settingsService);
                var wizard = new SetupWizardDialog { DataContext = wizardVm };

                if (wizard.ShowDialog() == true)
                {
                    settings = settingsService.Load();
                }
                else
                {
                    Shutdown();
                    return;
                }
            }

            // Create and show main window
            _indexManager = new IndexManager(settingsService);

            var mainVm = new MainViewModel(_indexManager, settings, settingsService);
            var mainWindow = new MainWindow { DataContext = mainVm };

            mainVm.RequestOpenSettings += () =>
            {
                var settingsVm = new SettingsViewModel(settingsService);
                settingsVm.ManualRescanAction = () => _indexManager.RequestManualRescan();
                var settingsDialog = new SettingsDialog
                {
                    DataContext = settingsVm,
                    Owner = mainWindow
                };

                if (settingsDialog.ShowDialog() == true)
                {
                    // Reload favorites in main VM (no restart needed for favorites-only changes)
                    mainVm.ReloadFavorites();

                    // Settings changed - restart the application for drive changes
                    var restartResult = MessageBox.Show(
                        "設定が保存されました。ドライブ設定の変更を適用するにはアプリケーションの再起動が必要です。\n\n今すぐ再起動しますか?",
                        "設定変更",
                        MessageBoxButton.YesNo,
                        MessageBoxImage.Question);

                    if (restartResult == MessageBoxResult.Yes)
                    {
                        RestartApp();
                    }
                }
            };

            mainVm.RequestOpenHelp += () =>
            {
                var helpDialog = new HelpDialog { Owner = mainWindow };
                helpDialog.ShowDialog();
            };

            mainVm.RequestOpenAbout += () =>
            {
                var aboutDialog = new AboutDialog { Owner = mainWindow };
                aboutDialog.ShowDialog();
            };

            MainWindow = mainWindow;
            ShutdownMode = ShutdownMode.OnMainWindowClose;
            mainWindow.Show();

            // Start indexing in the background
            _ = mainVm.StartIndexingAsync();
        }

        protected override void OnExit(ExitEventArgs e)
        {
            _indexManager?.Dispose();
            base.OnExit(e);
        }

        private static bool IsRunningAsAdmin()
        {
            try
            {
                var identity = WindowsIdentity.GetCurrent();
                var principal = new WindowsPrincipal(identity);
                return principal.IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch
            {
                return false;
            }
        }

        private static void RestartAsAdmin()
        {
            try
            {
                var processInfo = new ProcessStartInfo
                {
                    FileName = Process.GetCurrentProcess().MainModule.FileName,
                    UseShellExecute = true,
                    Verb = "runas"
                };
                Process.Start(processInfo);
            }
            catch (Exception)
            {
                // User cancelled UAC
            }
        }

        private void RestartApp()
        {
            try
            {
                Process.Start(Process.GetCurrentProcess().MainModule.FileName);
                Shutdown();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"再起動に失敗しました:\n{ex.Message}",
                    "エラー", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }
}
