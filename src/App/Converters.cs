using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace ModelCodex.App;

/// <summary>bool -> inverse bool (for IsEnabled = !IsBusy).</summary>
public sealed class InverseBoolConverter : IValueConverter
{
    public object Convert(object value, Type t, object parameter, CultureInfo c) => value is bool b ? !b : value;
    public object ConvertBack(object value, Type t, object parameter, CultureInfo c) => value is bool b ? !b : value;
}

/// <summary>bool -> Visibility (true => Visible, false => Collapsed).</summary>
public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type t, object parameter, CultureInfo c)
        => (value is bool b && b) ? Visibility.Visible : Visibility.Collapsed;
    public object ConvertBack(object value, Type t, object parameter, CultureInfo c)
        => value is Visibility v && v == Visibility.Visible;
}

/// <summary>int count -> Visibility (>0 => Visible, else Collapsed).</summary>
public sealed class CountToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type t, object parameter, CultureInfo c)
        => (value is int n && n > 0) ? Visibility.Visible : Visibility.Collapsed;
    public object ConvertBack(object value, Type t, object parameter, CultureInfo c) => 0;
}
