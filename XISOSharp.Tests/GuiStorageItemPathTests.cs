#if NET11_0_OR_GREATER
using System.Reflection;
using Avalonia.Platform.Storage;
using XISOSharp.Gui.Services;

namespace XISOSharp.Tests;

/// <summary>
/// Regression tests for converting Avalonia storage items to local paths.
/// Drive roots expose a relative <see cref="IStorageItem.Path"/>, for which
/// <see cref="Uri.LocalPath"/> throws; the GUI must use <c>TryGetLocalPath</c>
/// and treat a non-local item as a cancelled selection.
/// </summary>
public sealed class GuiStorageItemPathTests
{
    /// <summary>
    /// Selecting a drive root must return its full path, not throw: Avalonia's
    /// BCL folder has a relative <see cref="IStorageItem.Path"/> when the
    /// directory has no parent (the shape reported in the folder-picker bug).
    /// </summary>
    [Fact]
    public void TryGetLocalPath_DriveRoot_ReturnsFullPath()
    {
        DirectoryInfo root = new(Path.GetPathRoot(Path.GetTempPath())!);
        IStorageItem item = CreateBclStorageFolder(root);

        Assert.False(item.Path.IsAbsoluteUri);
        Assert.Equal(root.FullName, StorageItemPath.TryGetLocalPath(item));
    }

    /// <summary>An absent item has no path.</summary>
    [Fact]
    public void TryGetLocalPath_NullItem_ReturnsNull() =>
        Assert.Null(StorageItemPath.TryGetLocalPath(null));

    // BclStorageFolder is internal to Avalonia, but it is the provider that
    // produces the relative drive-root path this regression covers.
    private static IStorageItem CreateBclStorageFolder(DirectoryInfo directory)
    {
        Type type = typeof(IStorageItem).Assembly.GetType(
            "Avalonia.Platform.Storage.FileIO.BclStorageFolder", throwOnError: true)!;
        return (IStorageItem)Activator.CreateInstance(
            type,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null,
            args: [directory],
            culture: null)!;
    }
}
#endif
