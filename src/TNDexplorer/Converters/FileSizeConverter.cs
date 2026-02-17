using System;
using System.Globalization;
using System.Windows.Data;

namespace TNDexplorer.Converters
{
    public class FileSizeConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is long size)
            {
                if (size < 0) return "";
                if (size == 0) return "0 B";
                if (size >= 1L << 40) return $"{size / (double)(1L << 40):F1} TB";
                if (size >= 1L << 30) return $"{size / (double)(1L << 30):F1} GB";
                if (size >= 1L << 20) return $"{size / (double)(1L << 20):F1} MB";
                if (size >= 1L << 10) return $"{size / (double)(1L << 10):F1} KB";
                return $"{size} B";
            }
            return "";
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}
