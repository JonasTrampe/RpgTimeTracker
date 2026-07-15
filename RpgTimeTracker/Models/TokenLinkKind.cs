namespace RpgTimeTracker.Models;

/// <summary>
///     What a MapTokenViewModel is linked to - see MapTokenViewModel's doc comment for the
///     "single source of truth" rule this enables (Name/Portrait/Health read live from the
///     linked entry, never copied onto the token).
/// </summary>
public enum TokenLinkKind
{
    None,
    Character,
    PointOfInterest
}
