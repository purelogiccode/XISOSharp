namespace XISOSharp.Gui.Services;

/// <summary>
/// Classifies folder-picker outcomes that are user input rather than
/// application failures.
/// </summary>
internal static class FolderPickerDiagnostics
{
    /// <summary>
    /// User-facing explanation for a folder that vanished while the native
    /// picker was open.
    /// </summary>
    internal const string MissingSelectionMessage =
        "[GUI] The selected path is not an existing folder (it may have been moved, deleted, or be inaccessible). " +
        "Choose another folder or type its path directly.";

    /// <summary>
    /// Detects Avalonia's <c>BclStorageItem</c> rejection of a selected folder
    /// that no longer exists (it can be deleted from inside the native dialog).
    /// </summary>
    /// <param name="exception">Exception thrown by the storage provider.</param>
    /// <returns><c>true</c> when the selection was a missing directory.</returns>
    internal static bool IsMissingSelection(Exception exception) =>
        exception is ArgumentException { ParamName: "fileSystemInfo" };
}
