using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using PdfStampNgrokDesktop.Core;

namespace PdfStampNgrokDesktop.Converters;

public sealed class LinkIndicatorToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not LinkIndicator indicator)
        {
            return new SolidColorBrush(Color.FromRgb(193, 142, 0));
        }

        return indicator switch
        {
            LinkIndicator.Healthy => new SolidColorBrush(Color.FromRgb(47, 158, 94)),
            LinkIndicator.Degraded => new SolidColorBrush(Color.FromRgb(193, 142, 0)),
            LinkIndicator.Error => new SolidColorBrush(Color.FromRgb(226, 77, 77)),
            _ => new SolidColorBrush(Color.FromRgb(193, 142, 0)),
        };
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}
