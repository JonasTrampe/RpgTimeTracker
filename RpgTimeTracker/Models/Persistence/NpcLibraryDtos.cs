using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using RpgTimeTracker.Models;

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

    /// <summary>Additive migration: absent on any entry saved before this field existed
    ///     deserializes to the default (Npc), so existing Characters keep loading as NPCs.</summary>
    public CharacterKind Kind { get; set; } = CharacterKind.Npc;

    /// <summary>
    ///     Shared across all variants (see NpcGmInfoBlockDto) - private GM reference notes
    ///     (motivation, secrets, stat block) don't change with the NPC's mood/variant.
    /// </summary>
    public List<NpcGmInfoBlockDto> GmInfoBlocks { get; set; } = [];

    /// <summary>
    ///     C# name renamed from "State(s)" to "Variant(s)" (see NpcVariantEntryDto), but the
    ///     JSON key stays "States" so settings.json/session-library.json files saved before this
    ///     rename keep loading their characters' variants instead of silently losing them.
    /// </summary>
    [JsonPropertyName("States")]
    public List<NpcVariantEntryDto> Variants { get; set; } = [];

    /// <summary>
    ///     Which variant is currently "true" for this NPC - must match one of Variants' Id.
    ///     JSON key kept as "ActiveStateId" - see Variants' doc comment.
    /// </summary>
    [JsonPropertyName("ActiveStateId")]
    public Guid ActiveVariantId { get; set; }

    /// <summary>
    ///     Freeform Tag Ids attached to this character (see Tag) - shared across all
    ///     variants, same as GmInfoBlocks.
    /// </summary>
    public List<Guid> TagIds { get; set; } = [];
}

/// <summary>
///     A GM-named, ordered markdown section (e.g. "Motivation", "Secrets", "Combat stats") -
///     rendered via the same ClassIsland.Markdown.Avalonia control already used for the About
///     screen.
/// </summary>
public sealed class NpcGmInfoBlockDto
{
    public string Title { get; set; } = string.Empty;
    public string MarkdownBody { get; set; } = string.Empty;
}

/// <summary>
///     One named mood/variant of an NPC (e.g. "Neutral", "Angry", "Wounded"). Every field below is
///     nullable - null on a non-default variant means "inherit from the Default variant" (same pattern
///     as MapLibraryEntryDto's per-map fog-style overrides falling back to the global default -
///     see MainWindowViewModel.GetEffectiveFogStyle). ImageId/TokenImageId reference Media Library
///     entries by Id; SoundIds reference Sound Library entries by Id - none of these are owned
///     copies, just foreign keys (see NpcLibraryItemViewModel's doc comment for why this makes the
///     usage registry/deletion safeguard relevant here for the first time).
/// </summary>
public sealed class NpcVariantEntryDto
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public bool IsDefault { get; set; }

    public Guid? ImageId { get; set; }

    /// <summary>
    ///     Token resolution chain: TokenImageId (explicit token image) -> TokenIcon (a
    ///     Bootstrap icon, see VisualItemHelper) -> initials derived from the NPC's Name - see
    ///     NpcLibraryItemViewModel.GetEffectiveTokenImage/GetEffectiveTokenIcon/ResolvedTokenInitials.
    /// </summary>
    public Guid? TokenImageId { get; set; }

    public string? TokenIcon { get; set; }

    /// <summary>
    ///     GM-only in this pass - not yet transmitted to players over the network (see the
    ///     design conversation this feature was built from: "model now, wire later").
    /// </summary>
    public string? PlayerInfo { get; set; }

    /// <summary>
    ///     Null/absent = inherit the Default variant's sounds; present (even as an empty list)
    ///     = an explicit override, including "no sounds at all" for this variant.
    /// </summary>
    public List<Guid>? SoundIds { get; set; }

    /// <summary>
    ///     GM-visible-only freetext status/condition notes (e.g. "3 boxes Physical, 1 Stun") -
    ///     per-Variant since a "Wounded" mood naturally wants different notes than "Neutral". Not
    ///     sent to players; see StatusId for the separate map-token-visible status.
    /// </summary>
    public string? Health { get; set; }

    /// <summary>
    ///     References one of the active Theme's Statuses by Id (see StatusDefinitionDto) - not a
    ///     fixed enum, since different game systems have different status vocabularies. Null = no
    ///     status set. Resolved against whichever Theme happens to be active when read, so
    ///     switching Themes can change what this Id means/displays as.
    /// </summary>
    public string? StatusId { get; set; }
}