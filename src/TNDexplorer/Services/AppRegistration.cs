using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using Microsoft.Win32;

namespace TNDexplorer.Services
{
    /// <summary>
    /// Registers / unregisters the application in the Windows "Add/Remove Programs"
    /// registry so that corporate asset management tools (AssetView etc.) can detect it.
    ///
    /// Uses HKEY_CURRENT_USER (same approach as TND_AudioTranscription / whisper app,
    /// which is confirmed to be detected by AssetView in production).
    /// </summary>
    internal static class AppRegistration
    {
        private const string UninstallKeyPath =
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\TNDexplorer";

        private const string DisplayName = "TNDexplorer";
        private const string Publisher = "TND";

        /// <summary>
        /// Ensures the Uninstall registry key exists and is up to date.
        /// Called at every app startup (idempotent, ~1ms).
        /// If the EXE has been moved to a different folder, the paths are
        /// automatically updated on the next launch.
        /// </summary>
        public static void EnsureRegistered()
        {
            try
            {
                string exePath = Process.GetCurrentProcess().MainModule.FileName;
                string installDir = Path.GetDirectoryName(exePath);
                var version = Assembly.GetExecutingAssembly().GetName().Version;
                string versionStr = $"{version.Major}.{version.Minor}.{version.Build}";

                using (var key = Registry.CurrentUser.CreateSubKey(UninstallKeyPath))
                {
                    if (key == null) return;

                    key.SetValue("DisplayName", DisplayName);
                    key.SetValue("DisplayVersion", versionStr);
                    key.SetValue("Publisher", Publisher);
                    key.SetValue("InstallLocation", installDir);
                    key.SetValue("UninstallString",
                        $"\"{exePath}\" --uninstall");
                    key.SetValue("DisplayIcon", $"{exePath},0");
                    key.SetValue("NoModify", 1, RegistryValueKind.DWord);
                    key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
                }
            }
            catch (Exception ex)
            {
                // Non-fatal: asset management visibility is nice-to-have.
                // The app must continue to work even if registry write fails.
                Debug.WriteLine($"AppRegistration.EnsureRegistered failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Removes the Uninstall registry key and optionally cleans up
        /// user data (%LOCALAPPDATA%\TNDexplorer).
        /// Called when the app is invoked with --uninstall.
        /// </summary>
        public static void Unregister(bool deleteUserData)
        {
            // Remove registry key
            try
            {
                Registry.CurrentUser.DeleteSubKeyTree(
                    UninstallKeyPath, throwOnMissingSubKey: false);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"AppRegistration.Unregister registry failed: {ex.Message}");
            }

            // Optionally delete user data (settings + cache)
            if (deleteUserData)
            {
                try
                {
                    string appData = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        "TNDexplorer");
                    if (Directory.Exists(appData))
                        Directory.Delete(appData, recursive: true);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"AppRegistration.Unregister data cleanup failed: {ex.Message}");
                }
            }
        }
    }
}
