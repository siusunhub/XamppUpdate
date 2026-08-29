using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using XamppUpdate.Models;

namespace XamppUpdate.Converters
{
    public class BoolToVisibilityConverter : IValueConverter
    {
        public bool Invert { get; set; }

        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            bool val = value is bool b && b;
            if (Invert) val = !val;
            return val ? Visibility.Visible : Visibility.Collapsed;
        }

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            if (value is Visibility v)
            {
                bool val = v == Visibility.Visible;
                return Invert ? !val : val;
            }
            return false;
        }
    }

    public class InverseBooleanConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            return value is bool b && !b;
        }

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            return value is bool b && !b;
        }
    }

    public class ResolutionToBooleanConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            if (value is ConfigFileResolution res && parameter is string paramStr && Enum.TryParse<ConfigFileResolution>(paramStr, out var targetRes))
            {
                return res == targetRes;
            }
            return false;
        }

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            if (value is bool b && b && parameter is string paramStr && Enum.TryParse<ConfigFileResolution>(paramStr, out var targetRes))
            {
                return targetRes;
            }
            return Binding.DoNothing;
        }
    }
}
