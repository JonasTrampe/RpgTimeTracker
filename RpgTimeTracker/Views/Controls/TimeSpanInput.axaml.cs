using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;

namespace RpgTimeTracker.Views.Controls;

public partial class TimeSpanInput : UserControl
{
    public static readonly StyledProperty<string> TextProperty =
        AvaloniaProperty.Register<TimeSpanInput, string>(nameof(Text), "00.00:00:00",
            defaultBindingMode: BindingMode.TwoWay);

    public static readonly StyledProperty<bool> AllowEmptyProperty =
        AvaloniaProperty.Register<TimeSpanInput, bool>(nameof(AllowEmpty));

    public static readonly StyledProperty<int> LimitDaysProperty =
        AvaloniaProperty.Register<TimeSpanInput, int>(nameof(LimitDays), 120);
    
    public static readonly StyledProperty<int> LimitHoursProperty =
        AvaloniaProperty.Register<TimeSpanInput, int>(nameof(LimitHours), 23);

    public static readonly StyledProperty<int> LimitMinutesProperty =
        AvaloniaProperty.Register<TimeSpanInput, int>(nameof(LimitMinutes), 59);

    public static readonly StyledProperty<int> LimitSecondsProperty =
        AvaloniaProperty.Register<TimeSpanInput, int>(nameof(LimitSeconds), 59);

    private bool _updating;

    public TimeSpanInput()
    {
        InitializeComponent();

        DaysBox.TextChanged += (_, _) => UpdateTextFromBoxes();
        HoursBox.TextChanged += (_, _) => UpdateTextFromBoxes();
        MinutesBox.TextChanged += (_, _) => UpdateTextFromBoxes();
        SecondsBox.TextChanged += (_, _) => UpdateTextFromBoxes();

        DaysBox.PointerWheelChanged += (_, e) => ScrollBox(DaysBox, e, 0, LimitDays);
        HoursBox.PointerWheelChanged += (_, e) => ScrollBox(HoursBox, e, 0, LimitHours);
        MinutesBox.PointerWheelChanged += (_, e) => ScrollBox(MinutesBox, e, 0, LimitMinutes);
        SecondsBox.PointerWheelChanged += (_, e) => ScrollBox(SecondsBox, e, 0, LimitSeconds);

        UpdateBoxesFromText(Text);
    }

    public string Text
    {
        get => GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public bool AllowEmpty
    {
        get => GetValue(AllowEmptyProperty);
        set => SetValue(AllowEmptyProperty, value);
    }
    
    public int LimitDays
    {
        get => GetValue(LimitDaysProperty);
        set => SetValue(LimitDaysProperty, value);
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
        // the boxes: if AllowEmpty/Limit* attributes apply in XAML *after* Text (as they do for
        // the Alarm repeat-interval field), the initial Text="" push would otherwise have already
        // filled the boxes with clamped zeros (AllowEmpty was still false at that point), and
        // recomputing Text from those stale non-blank boxes here would permanently overwrite an
        // intentionally-empty bound value with "00:00:00".
        if ((change.Property == LimitDaysProperty ||
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
            if (AllowEmpty && string.IsNullOrWhiteSpace(text))
            {
                HoursBox.Text = string.Empty;
                MinutesBox.Text = string.Empty;
                SecondsBox.Text = string.Empty;
                return;
            }

            if (!TimeSpan.TryParse(text, out var parsed)) parsed = TimeSpan.Zero;

            DaysBox.Text = Math.Clamp(Math.Max(0, parsed.Days), 0, LimitDays).ToString();
            HoursBox.Text = Math.Clamp(Math.Max(0, parsed.Hours), 0, LimitHours).ToString();
            MinutesBox.Text = Math.Clamp(parsed.Minutes, 0, LimitMinutes).ToString("00");
            SecondsBox.Text = Math.Clamp(parsed.Seconds, 0, LimitSeconds).ToString("00");
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
            if (AllowEmpty  &&
                string.IsNullOrWhiteSpace(DaysBox.Text) &&
                string.IsNullOrWhiteSpace(HoursBox.Text) &&
                string.IsNullOrWhiteSpace(MinutesBox.Text) &&
                string.IsNullOrWhiteSpace(SecondsBox.Text))
            {
                Text = string.Empty;
                return;
            }

            var days = Math.Clamp(ParseNonNegative(DaysBox.Text), 0, LimitDays);
            var hours = Math.Clamp(ParseNonNegative(HoursBox.Text), 0, LimitHours);
            var minutes = Math.Clamp(ParseNonNegative(MinutesBox.Text), 0, LimitMinutes);
            var seconds = Math.Clamp(ParseNonNegative(SecondsBox.Text), 0, LimitSeconds);

            if (days > 0)
            {
                Text = $"{days}.{hours:00}:{minutes:00}:{seconds:00}";
            }
            else
            {
                Text = $"{hours:00}:{minutes:00}:{seconds:00}";
            }

        }
        finally
        {
            _updating = false;
        }
    }

    private void ScrollBox(TextBox box, PointerWheelEventArgs e, int min, int max)
    {
        var current = ParseNonNegative(box.Text);
        var next = Math.Clamp(current + (e.Delta.Y > 0 ? 1 : -1), min, max);
        box.Text = next.ToString(box == HoursBox ? "0" : "00");
        e.Handled = true;
    }

    private static int ParseNonNegative(string? value)
    {
        return int.TryParse(value, out var parsed) && parsed > 0 ? parsed : 0;
    }
}