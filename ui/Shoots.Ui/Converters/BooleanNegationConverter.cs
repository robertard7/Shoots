#nullable enable
using System;
using System.Globalization;
using System.Windows.Data;

namespace Shoots.UI.Converters;

/// <summary>
/// Inverts a boolean for WPF bindings.
/// </summary>
public sealed class BooleanNegationConverter : IValueConverter
{
    public static readonly BooleanNegationConverter Instance = new();

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is bool b ? !b : Binding.DoNothing;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => value is bool b ? !b : Binding.DoNothing;
}