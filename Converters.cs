using System.Windows;
using System.Windows.Data;
using Chistara.Services;

namespace Chistara;

public sealed class ArtworkPathConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        ArtworkImageCache.Load(value?.ToString(), 192) ?? DependencyProperty.UnsetValue;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
