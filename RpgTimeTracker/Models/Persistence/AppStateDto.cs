using System;
using System.Collections.Generic;
using RpgTimeTracker.Shared.Models;

namespace RpgTimeTracker.Models.Persistence;

/// <summary>
///     Serializable snapshot of the complete app state.
///     TimeSpan values are deliberately stored as long (ticks), since
///     System.Text.Json does not support TimeSpan without a custom converter.
/// </summary>
public class AppStateDto
{
    public int Version { get; set; } = 4;
    public DateTime CurrentGameTime { get; set; }
    public double SpeedMultiplier { get; set; } = 1.0;
    public bool IsClockRunning { get; set; }

    public List<TimerDto> Timers { get; set; } = [];
    public List<AlarmDto> Alarms { get; set; } = [];
    public List<IntervalEventDto> IntervalEvents { get; set; } = [];
    public List<JumpMarkerDto> JumpMarkers { get; set; } = [];
    public List<CalendarEntryDefinition> CalendarEntries { get; set; } = [];

    public string? Theme { get; set; }

    /// <summary>Global default sound for new items. Individual items store their sound separately.</summary>
    public string? Sound { get; set; }
}

public class TimerDto
{
    public string Name { get; set; } = "Timer";
    public string Icon { get; set; } = "Bootstrap: Clock";
    public string? Sound { get; set; }
    public int SoundRepeatCount { get; set; } = 1;
    public string? ColorHex { get; set; }
    public bool Blink { get; set; }
    public bool IsPlayerVisible { get; set; } = true;

    public long DurationTicks { get; set; }
    public long ElapsedTicks { get; set; }
    public bool IsRunning { get; set; }

    public string? TriggerMediaPath { get; set; }
    public string? TriggerMediaFileName { get; set; }
    public string? TriggerMediaKind { get; set; }
    public bool TriggerMediaFullscreen { get; set; }
    public bool TriggerMediaPauseClock { get; set; }
    public bool TriggerMediaLoop { get; set; }
}

public class AlarmDto
{
    public string Name { get; set; } = "Wecker";
    public string Icon { get; set; } = "Bootstrap: Alarm";
    public string? Sound { get; set; }
    public int SoundRepeatCount { get; set; } = 1;
    public string? ColorHex { get; set; }
    public bool Blink { get; set; }
    public bool IsPlayerVisible { get; set; } = true;

    public DateTime TriggerAt { get; set; }
    public long? RepeatIntervalTicks { get; set; }
    public bool IsTriggered { get; set; }

    public string? TriggerMediaPath { get; set; }
    public string? TriggerMediaFileName { get; set; }
    public string? TriggerMediaKind { get; set; }
    public bool TriggerMediaFullscreen { get; set; }
    public bool TriggerMediaPauseClock { get; set; }
    public bool TriggerMediaLoop { get; set; }
}

public class IntervalEventDto
{
    public string Name { get; set; } = "Intervall";
    public string Icon { get; set; } = "Bootstrap: Lightning";
    public string? Sound { get; set; }
    public int SoundRepeatCount { get; set; } = 1;
    public string? ColorHex { get; set; }
    public bool Blink { get; set; } = true;
    public bool IsPlayerVisible { get; set; } = true;

    public long IntervalTicks { get; set; }
    public long ActiveDurationTicks { get; set; }
    public int? MaxRepeats { get; set; }
    public long ElapsedTicks { get; set; }
    public bool IsRunning { get; set; }
    public bool IsCompleted { get; set; }

    public string? TriggerMediaPath { get; set; }
    public string? TriggerMediaFileName { get; set; }
    public string? TriggerMediaKind { get; set; }
    public bool TriggerMediaFullscreen { get; set; }
    public bool TriggerMediaPauseClock { get; set; }
    public bool TriggerMediaLoop { get; set; }
}

public class JumpMarkerDto
{
    public string Name { get; set; } = "Marke";
    public long TimeOfDayTicks { get; set; }
}