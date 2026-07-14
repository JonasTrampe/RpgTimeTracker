using System;
using System.Globalization;
using Avalonia.Data.Converters;

namespace RpgTimeTracker.Views.Converters;

/// <summary>
///     Multiplies a bound double (e.g. the host Window's Bounds.Height) by ConverterParameter
///     (a fraction, e.g. "0.6") - used to cap a text box's MaxHeight at a portion of the window's
///     current height instead of a fixed pixel value, so a long Markdown entry never grows to
///     dominate the whole visible area regardless of window size.
/// </summary>
public sealed class FractionOfConverter : IValueConverter
{
    public static readonly FractionOfConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not double amount) return value;
        if (parameter is not string s || !double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var fraction))
            return amount;

        return amount * fraction;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException("One-way only - MaxHeight is derived from window height, never written back.");
    }
}
