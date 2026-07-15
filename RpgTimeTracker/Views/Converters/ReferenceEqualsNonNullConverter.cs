using System;
using System.Collections.Generic;
using System.Globalization;
using Avalonia.Data.Converters;

namespace RpgTimeTracker.Views.Converters;

/// <summary>
///     Whether two bound reference-type values are the same non-null instance - unlike
///     Avalonia's built-in ObjectConverters.Equal, this is false when both values are null, which
///     matters for a "this row's item is the one currently playing/shown" check where "nothing is
///     selected yet" must not read as "matches nothing selected." Used as a two-value MultiBinding
///     converter, e.g. Scene bundle rows comparing a Playlist to MainWindowViewModel.CurrentPlaylist.
/// </summary>
public sealed class ReferenceEqualsNonNullConverter : IMultiValueConverter
{
    public static readonly ReferenceEqualsNonNullConverter Instance = new();

    public object? Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values.Count < 2) return false;

        return values[0] is not null && ReferenceEquals(values[0], values[1]);
    }
}
