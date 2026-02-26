using System;
using System.Globalization;
using System.Windows.Data;

namespace TNDexplorer.Converters
{
    /// <summary>
    /// Converts between int and bool for RadioButton binding.
    /// ConverterParameter specifies which int value corresponds to true.
    /// Usage: IsChecked="{Binding MyIntProp, Converter={StaticResource IntToBool}, ConverterParameter=0}"
    /// </summary>
    public class IntToBoolConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is int intValue && parameter != null)
            {
                if (int.TryParse(parameter.ToString(), out int target))
                    return intValue == target;
            }
            return false;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is bool boolValue && boolValue && parameter != null)
            {
                if (int.TryParse(parameter.ToString(), out int target))
                    return target;
            }
            return Binding.DoNothing;
        }
    }
}
