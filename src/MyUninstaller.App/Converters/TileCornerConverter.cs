using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace MyUninstaller.App.Converters;

/// <summary>Sudut ubin ikon sebanding ukurannya (sekitar 22%), seperti ubin ikon aplikasi.</summary>
public sealed class TileCornerConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is double size && size > 0 ? new CornerRadius(Math.Round(size * 0.22)) : new CornerRadius(8);

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
