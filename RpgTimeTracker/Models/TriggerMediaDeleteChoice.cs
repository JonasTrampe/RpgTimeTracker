namespace RpgTimeTracker.Models;

/// <summary>
///     Response to the warning shown when deleting a media library entry that is still
///     assigned as an event medium (see MainWindowViewModel.RemoveMediaLibraryItemAsync).
/// </summary>
public enum TriggerMediaDeleteChoice
{
    /// <summary>Cancel deletion, change nothing.</summary>
    Cancel,

    /// <summary>Remove the assignment from all affected items, then delete the library entry + file.</summary>
    RemoveFromItemsAndDelete,

    /// <summary>
    ///     Only remove the library entry; the file stays on disk so that the
    ///     affected items keep their event medium (but it can no longer be managed in the library).
    /// </summary>
    KeepInItemsRemoveFromLibraryOnly
}