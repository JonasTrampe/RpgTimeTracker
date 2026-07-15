using System;
using System.Globalization;
using Avalonia.Data.Converters;
using RpgTimeTracker.Services;
using RpgTimeTracker.Shared.Services.Localization;

namespace RpgTimeTracker.Views.Converters;

/// <summary>
///     Displays a localized label for the built-in tones (Pling/Bell/Digital/None) while the
///     underlying Sound property keeps its stable identifier - the identifier is what's actually
///     stored/serialized and compared against in SoundService.ResolvePath, so it must never change
///     with the UI language. Sound library entries (any other name) pass through unchanged, since
///     those are freely named by the GM.
/// </summary>
public sealed class SoundDisplayNameConverter : IValueConverter
{
    public static readonly SoundDisplayNameConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string name) return value;

        return name switch
        {
            SoundService.None => LocalizationService.Get("Sound.None"),
            SoundService.Pling => LocalizationService.Get("Sound.Pling"),
            SoundService.Bell => LocalizationService.Get("Sound.Bell"),
            SoundService.Digital => LocalizationService.Get("Sound.Digital"),
            _ => name
        };
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException(
            "Sound display names are one-way only - SelectedItem binds directly to the underlying identifier.");
    }
}