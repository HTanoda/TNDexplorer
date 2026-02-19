using System;
using System.Diagnostics;
using System.IO;
using System.Windows;
using TNDexplorer.Native;

namespace TNDexplorer.Services
{
    public class FileOperationService
    {
        public void OpenFile(string fullPath)
        {
            try
            {
                // Use explorer.exe to open files so they launch as the normal user,
                // not as admin. Some apps (e.g. Adobe Reader) fail under admin elevation.
                Process.Start("explorer.exe", $"\"{fullPath}\"");
            }
            catch (Exception ex)
            {
                MessageBox.Show($"ファイルを開けませんでした:\n{ex.Message}",
                    "エラー", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        public void OpenContainingFolder(string fullPath)
        {
            try
            {
                if (File.Exists(fullPath))
                {
                    Process.Start("explorer.exe", $"/select,\"{fullPath}\"");
                }
                else if (Directory.Exists(fullPath))
                {
                    Process.Start("explorer.exe", $"\"{fullPath}\"");
                }
                else
                {
                    string dir = Path.GetDirectoryName(fullPath);
                    if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
                        Process.Start("explorer.exe", $"\"{dir}\"");
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"フォルダを開けませんでした:\n{ex.Message}",
                    "エラー", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        public void CopyPathToClipboard(string fullPath)
        {
            try
            {
                Clipboard.SetText(fullPath);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"パスのコピーに失敗しました:\n{ex.Message}",
                    "エラー", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        public void CopyFileToClipboard(string fullPath)
        {
            try
            {
                var fileCollection = new System.Collections.Specialized.StringCollection();
                fileCollection.Add(fullPath);
                Clipboard.SetFileDropList(fileCollection);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"コピーに失敗しました:\n{ex.Message}",
                    "エラー", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        public void ShowProperties(string fullPath)
        {
            try
            {
                // Use SHObjectProperties Shell API for reliable property dialog
                bool result = NativeMethods.SHObjectProperties(
                    IntPtr.Zero,
                    NativeMethods.SHOP_FILEPATH,
                    fullPath,
                    null);

                if (!result)
                {
                    // Fallback: try ShellExecuteEx with "properties" verb
                    var info = new ProcessStartInfo
                    {
                        FileName = fullPath,
                        Verb = "properties",
                        UseShellExecute = true
                    };
                    Process.Start(info);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"プロパティの表示に失敗しました:\n{ex.Message}",
                    "エラー", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }
}
