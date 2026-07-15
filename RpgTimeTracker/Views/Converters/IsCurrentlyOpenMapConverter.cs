using System;
using System.Collections.Generic;
using System.Globalization;
using Avalonia.Data.Converters;
using RpgTimeTracker.ViewModels;

namespace RpgTimeTracker.Views.Converters;

/// <summary>
///     Whether a bound MapItemViewModel is the one currently shown to players - true only if it
///     is reference-equal to MainWindowViewModel.OpenMap AND IsMapOpenToPlayers is set (closing
///     a map leaves OpenMap pointing at the last-shown map, so both must be checked - see
///     IsSelectedMapOpenToPlayers's doc comment for the same reasoning). Used as a MultiBinding
///     converter (values: [this map, OpenMap, IsMapOpenToPlayers]) so a "Stop showing" button can
///     appear next to whichever bundle row's map is actually live, e.g. in the Scene editor.
/// </summary>
public sealed class IsCurrentlyOpenMapConverter : IMultiValueConverter
{
    public static readonly IsCurrentlyOpenMapConverter Instance = new();

    public object? Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values.Count < 3) return false;

        return values[0] is MapItemViewModel thisMap &&
               values[1] is MapItemViewModel openMap &&
               ReferenceEquals(thisMap, openMap) &&
               values[2] is true;
    }
}
