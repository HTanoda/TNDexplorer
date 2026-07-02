using System;
using System.Diagnostics;
using System.Reflection;
using System.Security.Principal;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
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
            RegisterGlobalExceptionHandlers();

            var version = Assembly.GetExecutingAssembly().GetName().Version;
            AppLog.Info("App", $"アプリ起動 v{version}");
            Task.Run(() => AppLog.CleanupOldLogs());

            // Handle --uninstall command (before admin check, so it works from "Programs & Features")
            var args = Environment.GetCommandLineArgs();
            for (int i = 1; i < args.Length; i++)
            {
                if (string.Equals(args[i], "--uninstall", StringComparison.OrdinalIgnoreCase))
                {
                    HandleUninstall();
                    Shutdown();
                    return;
                }
            }

            // Auto-elevate to admin (UAC prompt will appear)
            if (!IsRunningAsAdmin())
            {
                RestartAsAdmin();
                Shutdown();
                return;
            }

            // Register with Windows "Add/Remove Programs" for asset management tools (AssetView)
            AppRegistration.EnsureRegistered();

            // Prevent shutdown when wizard dialog closes before MainWindow is assigned
            ShutdownMode = ShutdownMode.OnExplicitShutdown;

            var settingsService = new SettingsService();
            var settings = settingsService.Load();

            // Apply saved theme before any window is created (idempotent).
            ThemeManager.Apply(settings.Theme);

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
                settingsVm.RescanCustomFolderAction = fp => _indexManager.RequestRescanCustomFolder(fp);
                settingsVm.ClearSearchHistoryAction = () => mainVm.ClearSearchHistoryFromSettings();
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

        private void RegisterGlobalExceptionHandlers()
        {
            DispatcherUnhandledException += (s, e) =>
            {
                try
                {
                    AppLog.Error("Unhandled", "DispatcherUnhandledException", e.Exception);
                    MessageBox.Show(
                        "予期しないエラーが発生しました。処理は継続します。\n" +
                        "詳細はログ (%LOCALAPPDATA%\\TNDexplorer\\logs) を確認してください。",
                        "エラー", MessageBoxButton.OK, MessageBoxImage.Warning);
                    e.Handled = true;
                }
                catch
                {
                    e.Handled = true;
                }
            };

            AppDomain.CurrentDomain.UnhandledException += (s, e) =>
            {
                try
                {
                    var ex = e.ExceptionObject as Exception;
                    AppLog.Error("Unhandled",
                        $"AppDomain.UnhandledException (IsTerminating={e.IsTerminating})", ex);
                }
                catch
                {
                }
            };

            TaskScheduler.UnobservedTaskException += (s, e) =>
            {
                try
                {
                    AppLog.Warn("Unhandled", "UnobservedTaskException: " + e.Exception);
                    e.SetObserved();
                }
                catch
                {
                }
            };
        }

        protected override void OnExit(ExitEventArgs e)
        {
            _indexManager?.Dispose();
            base.OnExit(e);
        }

        private void HandleUninstall()
        {
            var result = MessageBox.Show(
                "TNDexplorer をアンインストールしますか？\n\n" +
                "以下の処理を行います:\n" +
                "・レジストリ登録の解除（プログラムと機能から削除）\n" +
                "・設定ファイルとキャッシュの削除\n\n" +
                "※ EXEファイル自体は手動で削除してください。",
                "TNDexplorer アンインストール",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question,
                MessageBoxResult.No);

            if (result == MessageBoxResult.Yes)
            {
                AppRegistration.Unregister(deleteUserData: true);
                MessageBox.Show(
                    "アンインストールが完了しました。\n\n" +
                    "EXEファイルは手動で削除してください。",
                    "完了",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
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
                Process.Start(processInfo)?.Dispose();
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
                Process.Start(Process.GetCurrentProcess().MainModule.FileName)?.Dispose();
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
