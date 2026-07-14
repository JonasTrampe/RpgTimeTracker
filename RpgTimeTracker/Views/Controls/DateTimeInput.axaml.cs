using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;

namespace RpgTimeTracker.Views.Controls;

public partial class DateTimeInput : UserControl
{
    public static readonly StyledProperty<string> TextProperty =
        AvaloniaProperty.Register<DateTimeInput, string>(nameof(Text), DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
            defaultBindingMode: BindingMode.TwoWay);

    public static readonly StyledProperty<int> MinYearProperty =
        AvaloniaProperty.Register<DateTimeInput, int>(nameof(MinYear), 1);

    public static readonly StyledProperty<int> MaxYearProperty =
        AvaloniaProperty.Register<DateTimeInput, int>(nameof(MaxYear), 9999);

    public static readonly StyledProperty<int> LimitHoursProperty =
        AvaloniaProperty.Register<DateTimeInput, int>(nameof(LimitHours), 23);

    public static readonly StyledProperty<int> LimitMinutesProperty =
        AvaloniaProperty.Register<DateTimeInput, int>(nameof(LimitMinutes), 59);

    public static readonly StyledProperty<int> LimitSecondsProperty =
        AvaloniaProperty.Register<DateTimeInput, int>(nameof(LimitSeconds), 59);

    private bool _updating;

    public DateTimeInput()
    {
        InitializeComponent();

        YearBox.TextChanged += (_, _) => UpdateTextFromBoxes();
        MonthBox.TextChanged += (_, _) => UpdateTextFromBoxes();
        DayBox.TextChanged += (_, _) => UpdateTextFromBoxes();
        HourBox.TextChanged += (_, _) => UpdateTextFromBoxes();
        MinuteBox.TextChanged += (_, _) => UpdateTextFromBoxes();
        SecondBox.TextChanged += (_, _) => UpdateTextFromBoxes();

        YearBox.PointerWheelChanged += (_, e) => ScrollBox(YearBox, e, MinYear, MaxYear, "0000");
        MonthBox.PointerWheelChanged += (_, e) => ScrollBox(MonthBox, e, 1, 12, "00");
        DayBox.PointerWheelChanged += (_, e) => ScrollBox(DayBox, e, 1, CurrentMaxDay(), "00");
        HourBox.PointerWheelChanged += (_, e) => ScrollBox(HourBox, e, 0, LimitHours, "00");
        MinuteBox.PointerWheelChanged += (_, e) => ScrollBox(MinuteBox, e, 0, LimitMinutes, "00");
        SecondBox.PointerWheelChanged += (_, e) => ScrollBox(SecondBox, e, 0, LimitSeconds, "00");

        UpdateBoxesFromText(Text);
    }

    public string Text
    {
        get => GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public int MinYear
    {
        get => GetValue(MinYearProperty);
        set => SetValue(MinYearProperty, value);
    }

    public int MaxYear
    {
        get => GetValue(MaxYearProperty);
        set => SetValue(MaxYearProperty, value);
    }

    public int LimitHours
    {
        get => GetValue(LimitHoursProperty);
        set => SetValue(LimitHoursProperty, value);
    }

    public int LimitMinutes
    {
        get => GetValue(LimitMinutesProperty);
        set => SetValue(LimitMinutesProperty, value);
    }

    public int LimitSeconds
    {
        get => GetValue(LimitSecondsProperty);
        set => SetValue(LimitSecondsProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == TextProperty && !_updating)
            UpdateBoxesFromText(change.GetNewValue<string>() ?? string.Empty);

        // Re-render the boxes from the authoritative Text value rather than recomputing Text from
        // the boxes - see TimeSpanInput's identical fix for why recomputing from boxes here can
        // permanently overwrite a bound value with one derived from stale box content (order of
        // XAML attribute application relative to the Text binding).
        if ((change.Property == MinYearProperty ||
             change.Property == MaxYearProperty ||
             change.Property == LimitHoursProperty ||
             change.Property == LimitMinutesProperty ||
             change.Property == LimitSecondsProperty) &&
            !_updating)
            UpdateBoxesFromText(Text);
    }

    private void UpdateBoxesFromText(string text)
    {
        _updating = true;
        try
        {
            if (!DateTime.TryParse(text, out var parsed)) parsed = DateTime.Now;

            var year = Math.Clamp(parsed.Year, Math.Min(MinYear, MaxYear), Math.Max(MinYear, MaxYear));
            var month = Math.Clamp(parsed.Month, 1, 12);
            var day = Math.Clamp(parsed.Day, 1, DateTime.DaysInMonth(year, month));
            YearBox.Text = year.ToString("0000");
            MonthBox.Text = month.ToString("00");
            DayBox.Text = day.ToString("00");
            HourBox.Text = Math.Clamp(parsed.Hour, 0, LimitHours).ToString("00");
            MinuteBox.Text = Math.Clamp(parsed.Minute, 0, LimitMinutes).ToString("00");
            SecondBox.Text = Math.Clamp(parsed.Second, 0, LimitSeconds).ToString("00");
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
            var minYear = Math.Min(MinYear, MaxYear);
            var maxYear = Math.Max(MinYear, MaxYear);
            var year = Math.Clamp(ParseInt(YearBox.Text, DateTime.Now.Year), minYear, maxYear);
            var month = Math.Clamp(ParseInt(MonthBox.Text, 1), 1, 12);
            var day = Math.Clamp(ParseInt(DayBox.Text, 1), 1, DateTime.DaysInMonth(year, month));
            var hour = Math.Clamp(ParseInt(HourBox.Text, 0), 0, LimitHours);
            var minute = Math.Clamp(ParseInt(MinuteBox.Text, 0), 0, LimitMinutes);
            var second = Math.Clamp(ParseInt(SecondBox.Text, 0), 0, LimitSeconds);
            Text = new DateTime(year, month, day, hour, minute, second).ToString("yyyy-MM-dd HH:mm:ss");
        }
        finally
        {
            _updating = false;
        }
    }

    private int CurrentMaxDay()
    {
        var year = Math.Clamp(ParseInt(YearBox.Text, DateTime.Now.Year), Math.Min(MinYear, MaxYear),
            Math.Max(MinYear, MaxYear));
        var month = Math.Clamp(ParseInt(MonthBox.Text, 1), 1, 12);
        return DateTime.DaysInMonth(year, month);
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