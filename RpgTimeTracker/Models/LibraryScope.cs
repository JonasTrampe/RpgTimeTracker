namespace RpgTimeTracker.Models;

/// <summary>Which storage pool a library item belongs to - added as groundwork for the Session
///     concept (a folder-scoped campaign the GM can open/switch, see SessionService). Shared
///     items live in the always-present AppData library (ThemeSettingsService) and are reusable
///     across every campaign; SessionLocal items live inside one open session's folder and are
///     private to that campaign. With no session open, every item is Shared - this enum only
///     starts to matter once a session exists.</summary>
public enum LibraryScope
{
    Shared,
    SessionLocal
}
