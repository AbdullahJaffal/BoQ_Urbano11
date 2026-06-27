using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace UrbanoMetraj.BoQ.SmartAssembly.UI.Converters
{
    /// <summary>null → Collapsed, non-null → Visible (invert with ConverterParameter="invert").</summary>
    [ValueConversion(typeof(object), typeof(Visibility))]
    public class NullToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            bool isNull   = value == null;
            bool isInvert = "invert".Equals(parameter as string, StringComparison.OrdinalIgnoreCase);
            bool visible  = isInvert ? isNull : !isNull;
            return visible ? Visibility.Visible : Visibility.Collapsed;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => Binding.DoNothing;
    }

    /// <summary>false → Collapsed, true → Visible (invert with ConverterParameter="invert").</summary>
    [ValueConversion(typeof(bool), typeof(Visibility))]
    public class BoolToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            bool b        = value is bool bv && bv;
            bool isInvert = "invert".Equals(parameter as string, StringComparison.OrdinalIgnoreCase);
            return (b ^ isInvert) ? Visibility.Visible : Visibility.Collapsed;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => (value is Visibility v) && v == Visibility.Visible;
    }
}
