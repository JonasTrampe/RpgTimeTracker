using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using RpgTimeTracker.Shared.Services;

namespace RpgTimeTracker.Views.Controls;

/// <summary>
///     Y/M/D/H/M/S box-grid date-time entry, same UX as the retired DateTimeInput but with bounds
///     (month count, days-in-month, hours/day, minutes/hour, seconds/minute) taken from
///     CalendarService.Active instead of hardcoded Gregorian rules - so it stays correct under any
///     custom calendar. ShowTime=false hides the H:M:S boxes for date-only fields (e.g.
///     CalendarEntryViewModel.RepeatUntilText).
/// </summary>
public partial class CalendarDateInput : UserControl
{
    public static readonly StyledProperty<string> TextProperty =
        AvaloniaProperty.Register<CalendarDateInput, string>(nameof(Text), string.Empty,
            defaultBindingMode: BindingMode.TwoWay);

    public static readonly StyledProperty<bool> ShowTimeProperty =
        AvaloniaProperty.Register<CalendarDateInput, bool>(nameof(ShowTime), true);

    private bool _updating;

    public CalendarDateInput()
    {
        InitializeComponent();

        YearBox.TextChanged += (_, _) => UpdateTextFromBoxes();
        MonthBox.TextChanged += (_, _) => UpdateTextFromBoxes();
        DayBox.TextChanged += (_, _) => UpdateTextFromBoxes();
        HourBox.TextChanged += (_, _) => UpdateTextFromBoxes();
        MinuteBox.TextChanged += (_, _) => UpdateTextFromBoxes();
        SecondBox.TextChanged += (_, _) => UpdateTextFromBoxes();

        YearBox.PointerWheelChanged += (_, e) => ScrollBox(YearBox, e, -9999, 9999, "0000");
        MonthBox.PointerWheelChanged += (_, e) => ScrollBox(MonthBox, e, 1, MonthCount(), "00");
        DayBox.PointerWheelChanged += (_, e) => ScrollBox(DayBox, e, 1, CurrentMaxDay(), "00");
        HourBox.PointerWheelChanged += (_, e) => ScrollBox(HourBox, e, 0, MaxHour(), "00");
        MinuteBox.PointerWheelChanged += (_, e) => ScrollBox(MinuteBox, e, 0, MaxMinute(), "00");
        SecondBox.PointerWheelChanged += (_, e) => ScrollBox(SecondBox, e, 0, MaxSecond(), "00");

        UpdateBoxesFromText(Text);
    }

    public string Text
    {
        get => GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public bool ShowTime
    {
        get => GetValue(ShowTimeProperty);
        set => SetValue(ShowTimeProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == TextProperty && !_updating)
            UpdateBoxesFromText(change.GetNewValue<string>() ?? string.Empty);
    }

    private static int MonthCount()
    {
        var count = CalendarService.Active.Months.Count;
        return count > 0 ? count : 12;
    }

    private static int MaxHour()
    {
        return Math.Max(0, CalendarService.Active.HoursPerDay - 1);
    }

    private static int MaxMinute()
    {
        return Math.Max(0, CalendarService.Active.MinutesPerHour - 1);
    }

    private static int MaxSecond()
    {
        return Math.Max(0, CalendarService.Active.SecondsPerMinute - 1);
    }

    private void UpdateBoxesFromText(string text)
    {
        _updating = true;
        try
        {
            var calendar = CalendarService.Active;
            var parsed = ShowTime
                ? calendar.TryParseDateTimeText(text, out var instant)
                : calendar.TryParseDateText(text, out instant);
            if (!parsed) instant = calendar.FromCalendarDate(1, 0, 1, 0, 0, 0);

            var date = calendar.ToCalendarDate(instant);
            var internalYear = date.Year - calendar.YearZeroOffset;
            var maxDay = calendar.DaysInMonth(internalYear, date.MonthIndex);

            YearBox.Text = date.Year.ToString("0000");
            MonthBox.Text = (date.MonthIndex + 1).ToString("00");
            DayBox.Text = Math.Clamp(date.Day, 1, maxDay).ToString("00");
            HourBox.Text = Math.Clamp(date.Hour, 0, MaxHour()).ToString("00");
            MinuteBox.Text = Math.Clamp(date.Minute, 0, MaxMinute()).ToString("00");
            SecondBox.Text = Math.Clamp(date.Second, 0, MaxSecond()).ToString("00");
        }
        finally
        {
            _updating = false;
        }
    }

    private void UpdateTextFromBoxes()
    {
        if (_updating) return;

        _updating = true;
        try
        {
            var calendar = CalendarService.Active;
            var year = Math.Clamp(ParseInt(YearBox.Text, 1), -9999, 9999);
            var monthIndex = Math.Clamp(ParseInt(MonthBox.Text, 1), 1, MonthCount()) - 1;
            var internalYear = year - calendar.YearZeroOffset;
            var maxDay = calendar.DaysInMonth(internalYear, monthIndex);
            var day = Math.Clamp(ParseInt(DayBox.Text, 1), 1, maxDay);
            var hour = ShowTime ? Math.Clamp(ParseInt(HourBox.Text, 0), 0, MaxHour()) : 0;
            var minute = ShowTime ? Math.Clamp(ParseInt(MinuteBox.Text, 0), 0, MaxMinute()) : 0;
            var second = ShowTime ? Math.Clamp(ParseInt(SecondBox.Text, 0), 0, MaxSecond()) : 0;

            var instant = calendar.FromCalendarDate(year, monthIndex, day, hour, minute, second);
            Text = ShowTime ? calendar.FormatDateTimeText(instant) : calendar.FormatDateText(instant);
        }
        finally
        {
            _updating = false;
        }
    }

    private int CurrentMaxDay()
    {
        var calendar = CalendarService.Active;
        var year = Math.Clamp(ParseInt(YearBox.Text, 1), -9999, 9999);
        var monthIndex = Math.Clamp(ParseInt(MonthBox.Text, 1), 1, MonthCount()) - 1;
        return calendar.DaysInMonth(year - calendar.YearZeroOffset, monthIndex);
    }

    private void ScrollBox(TextBox box, PointerWheelEventArgs e, int min, int max, string format)
    {
        var current = ParseInt(box.Text, min);
        var next = Math.Clamp(current + (e.Delta.Y > 0 ? 1 : -1), min, max);
        box.Text = next.ToString(format);
        e.Handled = true;
    }

    private static int ParseInt(string? value, int fallback)
    {
        return int.TryParse(value, out var parsed) ? parsed : fallback;
    }
}