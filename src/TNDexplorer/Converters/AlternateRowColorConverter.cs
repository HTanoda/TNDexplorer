using System;
using System.Globalization;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;

namespace TNDexplorer.Converters
{
    /// <summary>
    /// Returns alternating background color based on the item's index in the ListView.
    /// Values[0] = the item itself (for AlternationIndex)
    /// Parameter = "true" if alternating is enabled
    /// </summary>
    public class AlternateRowColorConverter : IValueConverter
    {
        private static readonly SolidColorBrush WhiteBrush = new SolidColorBrush(Color.FromRgb(0xFF, 0xFF, 0xFF));
        private static readonly SolidColorBrush AltBrush = new SolidColorBrush(Color.FromRgb(0xF7, 0xF7, 0xF7));

        static AlternateRowColorConverter()
        {
            WhiteBrush.Freeze();
            AltBrush.Freeze();
        }

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is int index && index % 2 == 1)
                return AltBrush;
            return WhiteBrush;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotSupportedException();
        }
    }
}
