using System;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace RpgTimeTracker.Views.Converters;

/// <summary>Binds a #RRGGBB/#AARRGGBB hex string field (ColorHex) to Avalonia's ColorPicker.Color.</summary>
public sealed class HexColorConverter : IValueConverter
{
    public static readonly HexColorConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is string hex && !string.IsNullOrWhiteSpace(hex) && Color.TryParse(hex, out var color)) return color;
        return Colors.Gray;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return value is Color color ? color.ToString() : string.Empty;
    }
}