using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
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
            // Use Win32 clipboard API directly instead of WPF Clipboard.SetText.
            // WPF uses OLE clipboard (COM-based) which throws CLIPBRD_E_CANT_OPEN
            // in admin-elevated processes. Win32 API avoids this entirely.
            const int maxRetries = 5;
            const int retryDelayMs = 50;

            for (int i = 0; i < maxRetries; i++)
            {
                if (NativeSetClipboardText(fullPath))
                    return;

                Thread.Sleep(retryDelayMs);
            }

            MessageBox.Show("パスのコピーに失敗しました:\nクリップボードを開けませんでした。",
                "エラー", MessageBoxButton.OK, MessageBoxImage.Error);
        }

        public void CopyFileToClipboard(string fullPath)
        {
            ClipboardRetry(() =>
            {
                var fileCollection = new System.Collections.Specialized.StringCollection();
                fileCollection.Add(fullPath);
                Clipboard.SetFileDropList(fileCollection);
            }, "コピーに失敗しました");
        }

        /// <summary>
        /// Sets clipboard text using Win32 API (OpenClipboard/SetClipboardData).
        /// Returns true on success, false if clipboard is busy.
        /// </summary>
        private static bool NativeSetClipboardText(string text)
        {
            if (!NativeMethods.OpenClipboard(IntPtr.Zero))
                return false;

            try
            {
                NativeMethods.EmptyClipboard();

                int byteCount = (text.Length + 1) * 2; // UTF-16 + null terminator
                IntPtr hGlobal = NativeMethods.GlobalAlloc(
                    NativeMethods.GMEM_MOVEABLE, (UIntPtr)byteCount);

                if (hGlobal == IntPtr.Zero)
                    return false;

                IntPtr pGlobal = NativeMethods.GlobalLock(hGlobal);
                if (pGlobal == IntPtr.Zero)
                {
                    NativeMethods.GlobalFree(hGlobal);
                    return false;
                }

                try
                {
                    Marshal.Copy(text.ToCharArray(), 0, pGlobal, text.Length);
                    Marshal.WriteInt16(pGlobal, text.Length * 2, 0); // null terminator
                }
                finally
                {
                    NativeMethods.GlobalUnlock(hGlobal);
                }

                if (NativeMethods.SetClipboardData(NativeMethods.CF_UNICODETEXT, hGlobal) == IntPtr.Zero)
                {
                    NativeMethods.GlobalFree(hGlobal);
                    return false;
                }

                // SetClipboardData succeeded — ownership transferred to OS, don't free
                return true;
            }
            finally
            {
                NativeMethods.CloseClipboard();
            }
        }

        private static void ClipboardRetry(Action action, string errorTitle, int maxRetries = 5, int retryDelayMs = 50)
        {
            for (int i = 0; i < maxRetries; i++)
            {
                try
                {
                    action();
                    return;
                }
                catch (ExternalException) when (i < maxRetries - 1)
                {
                    Thread.Sleep(retryDelayMs);
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"{errorTitle}:\n{ex.Message}",
                        "エラー", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }
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
