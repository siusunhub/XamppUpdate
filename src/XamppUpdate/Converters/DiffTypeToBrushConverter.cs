using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using XamppUpdate.Models;

namespace XamppUpdate.Converters
{
    public class DiffTypeToBrushConverter : IValueConverter
    {
        public string TargetProperty { get; set; } = "Background"; // "Background", "Foreground", "LineNumberFg"

        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            var diffType = value is DiffLineType dt ? dt : DiffLineType.Unchanged;
            string property = (parameter as string) ?? TargetProperty;

            return diffType switch
            {
                DiffLineType.Inserted => property switch
                {
                    "Foreground" => new SolidColorBrush((Color)ColorConverter.ConvertFromString("#15803D")),
                    "LineNumberFg" => new SolidColorBrush((Color)ColorConverter.ConvertFromString("#16A34A")),
                    _ => new SolidColorBrush((Color)ColorConverter.ConvertFromString("#E6F9EC")) // Light soft green
                },
                DiffLineType.Deleted => property switch
                {
                    "Foreground" => new SolidColorBrush((Color)ColorConverter.ConvertFromString("#B91C1C")),
                    "LineNumberFg" => new SolidColorBrush((Color)ColorConverter.ConvertFromString("#DC2626")),
                    _ => new SolidColorBrush((Color)ColorConverter.ConvertFromString("#FDE8E8")) // Light soft red
                },
                DiffLineType.Modified => property switch
                {
                    "Foreground" => new SolidColorBrush((Color)ColorConverter.ConvertFromString("#B45309")),
                    "LineNumberFg" => new SolidColorBrush((Color)ColorConverter.ConvertFromString("#D97706")),
                    _ => new SolidColorBrush((Color)ColorConverter.ConvertFromString("#FEF3C7")) // Light amber
                },
                DiffLineType.EmptyPlaceholder => property switch
                {
                    "Foreground" => Brushes.Transparent,
                    "LineNumberFg" => Brushes.Transparent,
                    _ => new SolidColorBrush((Color)ColorConverter.ConvertFromString("#F1F5F9")) // Soft slate placeholder
                },
                _ => property switch
                {
                    "Foreground" => new SolidColorBrush((Color)ColorConverter.ConvertFromString("#1E293B")),
                    "LineNumberFg" => new SolidColorBrush((Color)ColorConverter.ConvertFromString("#94A3B8")),
                    _ => Brushes.Transparent
                }
            };
        }

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}
