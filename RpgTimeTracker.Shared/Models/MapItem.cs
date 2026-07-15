using System;
using System.Collections.Generic;

namespace RpgTimeTracker.Shared.Models;

/// <summary>
///     One floor/layer of a Map - an image plus the grid geometry used to interpret
///     a FogMask over it. The "starting" fog template lives in the Map Library (see
///     MapLibraryEntryDto in ThemeSettingsService) and the "current/live" fog lives
///     in the save file (AppStateDto.MapProgress) - neither is part of this class,
///     since both are storage-tier-specific, not floor identity.
/// </summary>
public sealed class MapFloor
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "Floor";
    public string ImagePath { get; set; } = string.Empty;
    public int CellSizePx { get; set; } = 32;
    public int GridWidth { get; set; }
    public int GridHeight { get; set; }
}

/// <summary>A named map (e.g. a house), made of one or more floors.</summary>
public sealed class MapItem
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "Map";
    public List<MapFloor> Floors { get; set; } = [];
}