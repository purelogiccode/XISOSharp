#if NET11_0_OR_GREATER
using XISOSharp.Gui.Services;

namespace XISOSharp.Tests;

/// <summary>
/// Regression tests for the GUI folder-picker failure classifier: Avalonia
/// rejects a folder deleted while the native dialog is open and the GUI must
/// treat that as a cancelled selection, not an application error.
/// </summary>
public sealed class GuiFolderPickerTests
{
    /// <summary>Verifies the exception shape thrown by Avalonia's BCL provider.</summary>
    [Fact]
    public void IsMissingSelection_RecognizesDeletedFolderException()
    {
        ArgumentException exception = CreateArgumentException("fileSystemInfo");

        Assert.True(FolderPickerDiagnostics.IsMissingSelection(exception));
    }

    /// <summary>Verifies unrelated argument failures stay in the error path.</summary>
    [Fact]
    public void IsMissingSelection_IgnoresOtherArgumentFailures()
    {
        ArgumentException exception = CreateArgumentException("path");

        Assert.False(FolderPickerDiagnostics.IsMissingSelection(exception));
    }

    /// <summary>Verifies non-argument failures stay in the error path.</summary>
    [Fact]
    public void IsMissingSelection_IgnoresOtherExceptions()
    {
        Assert.False(FolderPickerDiagnostics.IsMissingSelection(new InvalidOperationException("picker failed")));
    }

    // InspectCode's NotResolvedInText inspection treats the literal paramName
    // argument as a symbol reference; pass it as a variable instead.
    private static ArgumentException CreateArgumentException(string parameterName) =>
        new("The selected path is not a valid folder.", parameterName);
}
#endif
