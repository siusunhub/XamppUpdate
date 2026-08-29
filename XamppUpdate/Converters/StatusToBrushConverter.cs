using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using XamppUpdate.Models;

namespace XamppUpdate.Converters
{
    public class StatusToBrushConverter : IValueConverter
    {
        public string TargetType { get; set; } = "Background"; // "Background", "Foreground", "Border", "Dot"

        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            var status = value is ServiceStatus s ? s : ServiceStatus.Unknown;
            string property = (parameter as string) ?? TargetType;

            return status switch
            {
                ServiceStatus.Running => property switch
                {
                    "Foreground" or "Dot" => new SolidColorBrush((Color)ColorConverter.ConvertFromString("#15803D")),
                    "Border" => new SolidColorBrush((Color)ColorConverter.ConvertFromString("#86EFAC")),
                    _ => new SolidColorBrush((Color)ColorConverter.ConvertFromString("#DCFCE7")) // Background
                },
                ServiceStatus.Stopped => property switch
                {
                    "Foreground" or "Dot" => new SolidColorBrush((Color)ColorConverter.ConvertFromString("#475569")),
                    "Border" => new SolidColorBrush((Color)ColorConverter.ConvertFromString("#CBD5E1")),
                    _ => new SolidColorBrush((Color)ColorConverter.ConvertFromString("#F1F5F9"))
                },
                ServiceStatus.UnderConstruction => property switch
                {
                    "Foreground" or "Dot" => new SolidColorBrush((Color)ColorConverter.ConvertFromString("#B45309")),
                    "Border" => new SolidColorBrush((Color)ColorConverter.ConvertFromString("#FDE68A")),
                    _ => new SolidColorBrush((Color)ColorConverter.ConvertFromString("#FEF3C7"))
                },
                ServiceStatus.Updating => property switch
                {
                    "Foreground" or "Dot" => new SolidColorBrush((Color)ColorConverter.ConvertFromString("#0369A1")),
                    "Border" => new SolidColorBrush((Color)ColorConverter.ConvertFromString("#BAE6FD")),
                    _ => new SolidColorBrush((Color)ColorConverter.ConvertFromString("#E0F2FE"))
                },
                ServiceStatus.Error or ServiceStatus.NotInstalled => property switch
                {
                    "Foreground" or "Dot" => new SolidColorBrush((Color)ColorConverter.ConvertFromString("#B91C1C")),
                    "Border" => new SolidColorBrush((Color)ColorConverter.ConvertFromString("#FECACA")),
                    _ => new SolidColorBrush((Color)ColorConverter.ConvertFromString("#FEE2E2"))
                },
                _ => property switch
                {
                    "Foreground" or "Dot" => new SolidColorBrush((Color)ColorConverter.ConvertFromString("#64748B")),
                    "Border" => new SolidColorBrush((Color)ColorConverter.ConvertFromString("#E2E8F0")),
                    _ => new SolidColorBrush((Color)ColorConverter.ConvertFromString("#F8FAFC"))
                }
            };
        }

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}
