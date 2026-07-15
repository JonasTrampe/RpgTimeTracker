namespace RpgTimeTracker.Models;

/// <summary>
///     Whether a Characters-library entry represents an NPC or a player character - see
///     NpcLibraryItemViewModel.Kind. Reuses the existing NPC entry type rather than a separate
///     model (portrait/token image/Variants/sounds/tags all apply identically to a PC) - see
///     design-decisions.md's "Characters gain a Kind flag instead of a separate PC model" entry.
/// </summary>
public enum CharacterKind
{
    Npc,
    Pc
}
