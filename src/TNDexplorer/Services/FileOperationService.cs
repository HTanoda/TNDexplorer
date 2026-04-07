using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows;
using TNDexplorer.Models;
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
            // Use Win32 clipboard API directly instead of WPF Clipboard.SetFileDropList.
            // WPF uses OLE clipboard (COM-based) which throws CLIPBRD_E_CANT_OPEN
            // in admin-elevated processes.
            const int maxRetries = 5;
            const int retryDelayMs = 50;

            for (int i = 0; i < maxRetries; i++)
            {
                if (NativeSetClipboardFileDropList(fullPath))
                    return;

                Thread.Sleep(retryDelayMs);
            }

            MessageBox.Show("コピーに失敗しました:\nクリップボードを開けませんでした。",
                "エラー", MessageBoxButton.OK, MessageBoxImage.Error);
        }

        /// <summary>
        /// Sets clipboard file drop list using Win32 API (CF_HDROP format).
        /// Returns true on success, false if clipboard is busy.
        /// </summary>
        private static bool NativeSetClipboardFileDropList(string filePath)
        {
            if (!NativeMethods.OpenClipboard(IntPtr.Zero))
                return false;

            try
            {
                NativeMethods.EmptyClipboard();

                // DROPFILES structure: 20 bytes header + file path (UTF-16) + double null terminator
                int headerSize = 20; // sizeof(DROPFILES)
                int fileBytes = (filePath.Length + 1) * 2; // file path + null terminator (UTF-16)
                int totalSize = headerSize + fileBytes + 2; // + final null terminator

                IntPtr hGlobal = NativeMethods.GlobalAlloc(
                    NativeMethods.GMEM_MOVEABLE | NativeMethods.GMEM_ZEROINIT,
                    (UIntPtr)totalSize);

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
                    // DROPFILES.pFiles = offset to file list (20 = header size)
                    Marshal.WriteInt32(pGlobal, 0, headerSize);
                    // DROPFILES.pt.x = 0, pt.y = 0 (already zeroed)
                    // DROPFILES.fNC = FALSE (already zeroed)
                    // DROPFILES.fWide = TRUE (Unicode)
                    Marshal.WriteInt32(pGlobal, 16, 1);

                    // Write file path after header
                    IntPtr pFiles = new IntPtr(pGlobal.ToInt64() + headerSize);
                    Marshal.Copy(filePath.ToCharArray(), 0, pFiles, filePath.Length);
                    // Null terminators already zeroed by GMEM_ZEROINIT
                }
                finally
                {
                    NativeMethods.GlobalUnlock(hGlobal);
                }

                if (NativeMethods.SetClipboardData(NativeMethods.CF_HDROP, hGlobal) == IntPtr.Zero)
                {
                    NativeMethods.GlobalFree(hGlobal);
                    return false;
                }

                return true;
            }
            finally
            {
                NativeMethods.CloseClipboard();
            }
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

        public void ExportResultsToCsv(IReadOnlyList<FileResultItem> results, string filePath)
        {
            try
            {
                using (var writer = new StreamWriter(filePath, false, new UTF8Encoding(true)))
                {
                    // CSV header
                    writer.WriteLine("ファイル名,フォルダ,サイズ(バイト),更新日時,拡張子");

                    foreach (var item in results)
                    {
                        writer.Write(EscapeCsvField(item.Name));
                        writer.Write(',');
                        writer.Write(EscapeCsvField(item.Path));
                        writer.Write(',');
                        if (!item.IsDirectory)
                            writer.Write(item.Size);
                        writer.Write(',');
                        writer.Write(item.Modified.ToString("yyyy-MM-dd HH:mm:ss"));
                        writer.Write(',');
                        writer.Write(EscapeCsvField(item.Extension));
                        writer.WriteLine();
                    }
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"CSVファイルの保存に失敗しました:\n{ex.Message}",
                    "エラー", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private static string EscapeCsvField(string value)
        {
            if (string.IsNullOrEmpty(value))
                return "";

            if (value.IndexOfAny(new[] { ',', '"', '\n', '\r' }) >= 0)
            {
                return "\"" + value.Replace("\"", "\"\"") + "\"";
            }
            return value;
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
