using System;
using System.Collections.Generic;

namespace RpgTimeTracker.Models.Persistence;

/// <summary>
///     Serializable shape of one entry in the Characters (NPC) library - see
///     NpcLibraryItemViewModel. Lives alongside the other hoisted library entry DTOs
///     (LibraryEntryDtos.cs) so both the Shared Library (ThemeSettingsService) and a Session's
///     own library (SessionService) can read/write the same shape.
/// </summary>
public sealed class NpcLibraryEntryDto
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;

    /// <summary>Shared across all states (see NpcGmInfoBlockDto) - private GM reference notes
    ///     (motivation, secrets, stat block) don't change with the NPC's mood/state.</summary>
    public List<NpcGmInfoBlockDto> GmInfoBlocks { get; set; } = [];

    public List<NpcStateEntryDto> States { get; set; } = [];

    /// <summary>Which state is currently "true" for this NPC - must match one of States' Id.</summary>
    public Guid ActiveStateId { get; set; }
}

/// <summary>A GM-named, ordered markdown section (e.g. "Motivation", "Secrets", "Combat stats") -
///     rendered via the same ClassIsland.Markdown.Avalonia control already used for the About
///     screen.</summary>
public sealed class NpcGmInfoBlockDto
{
    public string Title { get; set; } = string.Empty;
    public string MarkdownBody { get; set; } = string.Empty;
}

/// <summary>
///     One named mood/variant of an NPC (e.g. "Neutral", "Angry", "Wounded"). Every field below is
///     nullable - null on a non-default state means "inherit from the Default state" (same pattern
///     as MapLibraryEntryDto's per-map fog-style overrides falling back to the global default -
///     see MainWindowViewModel.GetEffectiveFogStyle). ImageId/TokenImageId reference Media Library
///     entries by Id; SoundIds reference Sound Library entries by Id - none of these are owned
///     copies, just foreign keys (see NpcLibraryItemViewModel's doc comment for why this makes the
///     usage registry/deletion safeguard relevant here for the first time).
/// </summary>
public sealed class NpcStateEntryDto
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public bool IsDefault { get; set; }

    public Guid? ImageId { get; set; }

    /// <summary>Token resolution chain: TokenImageId (explicit token image) -> TokenIcon (a
    ///     Bootstrap icon, see VisualItemHelper) -> initials derived from the NPC's Name - see
    ///     NpcLibraryItemViewModel.GetEffectiveTokenImage/GetEffectiveTokenIcon/ResolvedTokenInitials.</summary>
    public Guid? TokenImageId { get; set; }

    public string? TokenIcon { get; set; }

    /// <summary>GM-only in this pass - not yet transmitted to players over the network (see the
    ///     design conversation this feature was built from: "model now, wire later").</summary>
    public string? PlayerInfo { get; set; }

    /// <summary>Null/absent = inherit the Default state's sounds; present (even as an empty list)
    ///     = an explicit override, including "no sounds at all" for this state.</summary>
    public List<Guid>? SoundIds { get; set; }
}
