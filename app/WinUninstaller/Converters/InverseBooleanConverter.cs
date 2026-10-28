using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace WinUninstaller.Converters;

public sealed class InverseBooleanConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is bool enabled && !enabled;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => value is bool enabled && !enabled;
}

public sealed class InverseBooleanToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is bool enabled && !enabled ? Visibility.Visible : Visibility.Collapsed;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is Visibility visibility && visibility != Visibility.Visible;
}
