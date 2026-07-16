namespace RpgTimeTracker.Models;

/// <summary>
///     GM-configurable game clock behavior while an initiative order (#70) is running - see
///     MainWindowViewModel.InitiativeClockMode/AdvanceInitiativeRound.
/// </summary>
public enum InitiativeClockMode
{
    /// <summary>The clock is paused for the duration of the tracked combat, resumed on stop (if it was running before).</summary>
    Freeze,

    /// <summary>The clock keeps running normally, but also jumps forward by a fixed number of game-seconds once per Round.</summary>
    AdvancePerRound
}
