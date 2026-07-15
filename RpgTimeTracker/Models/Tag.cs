using System;

namespace RpgTimeTracker.Models;

/// <summary>
///     A freeform label a GM can attach to any library item (Media/Sound/Music/Map/NPC) via that
///     item's TagIds - separate from Scene membership (a different, explicit mechanism - see
///     design-decisions.md). Tags themselves are a single flat, campaign-wide list (not
///     Shared-vs-SessionLocal like the libraries they tag): unlike a media file, a tag carries no
///     heavyweight content, so there's little benefit to scoping it per-session, and a flat list
///     keeps tag filtering simple regardless of which session is currently open.
/// </summary>
public sealed class Tag
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public string ColorHex { get; set; } = string.Empty;
}
