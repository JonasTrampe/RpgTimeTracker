namespace RpgTimeTracker.Models;

/// <summary>See MapTokenViewModel.IsVisibleToPlayers for how these three modes resolve.</summary>
public enum TokenRevealMode
{
    AlwaysVisible,
    HiddenUntilRevealed,
    GmOnly
}
