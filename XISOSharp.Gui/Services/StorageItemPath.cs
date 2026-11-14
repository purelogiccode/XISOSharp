namespace XISOSharp.Gui.Services;

using Avalonia.Platform.Storage;
using Serilog;

/// <summary>
/// Converts picked or dropped Avalonia storage items to local filesystem paths.
/// </summary>
/// <remarks>
/// <see cref="IStorageItem.Path"/> can be a relative URI: Avalonia's BCL
/// provider returns one for drive roots (a <see cref="DirectoryInfo"/> without
/// a parent), and <see cref="Uri.LocalPath"/> throws
/// <see cref="InvalidOperationException"/> for relative URIs. The
/// <c>TryGetLocalPath</c> extension resolves the provider's underlying
/// filesystem metadata instead and returns <c>null</c> when the item has no
/// local path.
/// </remarks>
internal static class StorageItemPath
{
    /// <summary>
    /// Returns the local filesystem path of <paramref name="item"/>, or
    /// <c>null</c> when the storage provider exposes only a virtual or relative
    /// URI. Never throws.
    /// </summary>
    /// <param name="item">Picked or dropped storage item.</param>
    /// <returns>The full local path, or <c>null</c>.</returns>
    internal static string? TryGetLocalPath(IStorageItem? item)
    {
        try
        {
            return item?.TryGetLocalPath();
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Reading a storage item's local path failed");
            return null;
        }
    }
}
