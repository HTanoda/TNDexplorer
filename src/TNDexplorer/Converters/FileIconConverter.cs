using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Data;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using TNDexplorer.Native;

namespace TNDexplorer.Converters
{
    /// <summary>
    /// Converts a file extension or "folder" marker to a small shell icon ImageSource.
    /// Uses SHGetFileInfo with SHGFI_USEFILEATTRIBUTES for fast lookup (no disk access).
    /// Icons are cached by extension.
    /// </summary>
    public class FileIconConverter : IMultiValueConverter
    {
        private static readonly ConcurrentDictionary<string, ImageSource> _iconCache
            = new ConcurrentDictionary<string, ImageSource>(StringComparer.OrdinalIgnoreCase);

        private const uint FILE_ATTRIBUTE_NORMAL = 0x80;
        private const uint FILE_ATTRIBUTE_DIRECTORY = 0x10;

        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        {
            if (values == null || values.Length < 2)
                return null;

            string name = values[0] as string;
            bool isDirectory = values[1] is bool b && b;

            if (string.IsNullOrEmpty(name))
                return null;

            string cacheKey;
            if (isDirectory)
            {
                cacheKey = "<folder>";
            }
            else
            {
                int dot = name.LastIndexOf('.');
                cacheKey = dot >= 0 ? name.Substring(dot).ToLowerInvariant() : "<no_ext>";
            }

            return _iconCache.GetOrAdd(cacheKey, key => LoadIcon(key, isDirectory));
        }

        private static ImageSource LoadIcon(string key, bool isDirectory)
        {
            try
            {
                var shfi = new SHFILEINFO();
                uint flags = NativeConstants.SHGFI_ICON | NativeConstants.SHGFI_SMALLICON
                           | NativeConstants.SHGFI_USEFILEATTRIBUTES;

                string fakeName;
                uint fileAttr;

                if (isDirectory || key == "<folder>")
                {
                    fakeName = "folder";
                    fileAttr = FILE_ATTRIBUTE_DIRECTORY;
                }
                else
                {
                    fakeName = "file" + (key == "<no_ext>" ? "" : key);
                    fileAttr = FILE_ATTRIBUTE_NORMAL;
                }

                IntPtr result = NativeMethods.SHGetFileInfo(
                    fakeName,
                    fileAttr,
                    ref shfi,
                    (uint)Marshal.SizeOf(typeof(SHFILEINFO)),
                    flags);

                if (result != IntPtr.Zero && shfi.hIcon != IntPtr.Zero)
                {
                    var iconSource = Imaging.CreateBitmapSourceFromHIcon(
                        shfi.hIcon,
                        Int32Rect.Empty,
                        BitmapSizeOptions.FromEmptyOptions());

                    iconSource.Freeze(); // Important for cross-thread access
                    NativeMethods.DestroyIcon(shfi.hIcon);

                    return iconSource;
                }
            }
            catch
            {
                // Fallback: return null (no icon shown)
            }

            return null;
        }

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}
