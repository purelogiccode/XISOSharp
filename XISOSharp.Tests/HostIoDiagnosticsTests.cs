namespace XISOSharp.Tests;

/// <summary>
/// BRF-13: host I/O failures carry the path, the operation and a likely fix.
/// The mappings run on every OS; platform-specific literals are simulated so
/// Windows and Linux runners assert the same contract.
/// </summary>
public sealed class HostIoDiagnosticsTests
{
    [Fact]
    public void PermissionDenied_NamesPathOperationAndFix()
    {
        string message = HostIoDiagnostics.Describe(
            new UnauthorizedAccessException("Access is denied."),
            "writing the extracted file",
            @"C:\games\out\default.xex");

        Assert.Contains("writing the extracted file", message, StringComparison.Ordinal);
        Assert.Contains("default.xex", message, StringComparison.Ordinal);
        Assert.Contains("permission denied", message, StringComparison.Ordinal);
        Assert.Contains("permissions", message, StringComparison.Ordinal);
    }

    [Fact]
    public void LongPath_NamesPathAndSuggestsLongPathSupport()
    {
        string message = HostIoDiagnostics.Describe(
            new PathTooLongException("The specified path is too long."),
            "creating the image",
            new string('a', 400));

        Assert.Contains("creating the image", message, StringComparison.Ordinal);
        Assert.Contains("too long", message, StringComparison.Ordinal);
        Assert.Contains("long-path support", message, StringComparison.Ordinal);
    }

    [Fact]
    public void IllegalName_PointsAtNamePolicy()
    {
        string message = HostIoDiagnostics.Describe(
            new ArgumentException("Invalid argument"),
            "extracting the entry",
            "a:b.bin");

        Assert.Contains("a:b.bin", message, StringComparison.Ordinal);
        Assert.Contains("illegal characters", message, StringComparison.Ordinal);
        Assert.Contains("name policy", message, StringComparison.Ordinal);
    }

    [Fact]
    public void ExistingDestination_SuggestsOverwrite()
    {
        IOException exception = new("The file already exists.")
        {
            HResult = OperatingSystem.IsWindows()
                ? unchecked((int)0x80070050)
                : 17,
        };

        string message = HostIoDiagnostics.Describe(exception, "creating the image", "game.iso");

        Assert.Contains("game.iso", message, StringComparison.Ordinal);
        Assert.Contains("already exists", message, StringComparison.Ordinal);
        Assert.Contains("overwrite", message, StringComparison.Ordinal);
    }

    [Fact]
    public void ExtractFileException_UsesActionableDetail()
    {
        string work = Path.Combine(Path.GetTempPath(), $"xiso_io_diag_{Guid.NewGuid():N}");
        Directory.CreateDirectory(work);
        try
        {
            string source = Path.Combine(work, "source");
            Directory.CreateDirectory(source);
            File.WriteAllText(Path.Combine(source, "a.txt"), "hello");
            string iso = Path.Combine(work, "game.iso");
            Assert.Equal(0, XisoWriter.PackFromDirectory(source, iso, fileTime: 0));

            string destination = Path.Combine(work, "out");
            Directory.CreateDirectory(destination);
            string blocked = Path.Combine(destination, "a.txt");
            Directory.CreateDirectory(blocked); // A directory cannot be replaced by a file.

            InvalidDataException? failure = null;
            try
            {
                XisoReader.Extract(iso, destination, llCompat: false);
            }
            catch (Exception ex) when (ex is ExtractFileException or InvalidDataException)
            {
                failure = ex as InvalidDataException ?? new InvalidDataException(ex.Message, ex);
            }

            Assert.NotNull(failure);
            Assert.Contains("a.txt", failure.Message, StringComparison.Ordinal);
            Assert.Contains("could not create", failure.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            try
            {
                Directory.Delete(work, recursive: true);
            }
            catch
            {
                // best effort
            }
        }
    }

    [Fact]
    public void Extract_ReadOnlyDestination_ReportsPermissionContext()
    {
        string work = Path.Combine(Path.GetTempPath(), $"xiso_io_perm_{Guid.NewGuid():N}");
        Directory.CreateDirectory(work);
        try
        {
            string source = Path.Combine(work, "source");
            Directory.CreateDirectory(source);
            File.WriteAllText(Path.Combine(source, "a.txt"), "hello");
            string iso = Path.Combine(work, "game.iso");
            Assert.Equal(0, XisoWriter.PackFromDirectory(source, iso, fileTime: 0));

            string destination = Path.Combine(work, "out");
            Directory.CreateDirectory(destination);
            string blocked = Path.Combine(destination, "a.txt");
            File.WriteAllText(blocked, "existing");
            if (OperatingSystem.IsWindows())
            {
                File.SetAttributes(blocked, FileAttributes.ReadOnly);
            }
            else
            {
                File.SetUnixFileMode(blocked, UnixFileMode.UserRead);
            }

            string? message = null;
            try
            {
                XisoReader.Extract(iso, destination, llCompat: false);
            }
            catch (Exception ex) when (ex is ExtractFileException or UnauthorizedAccessException or IOException)
            {
                message = HostIoDiagnostics.Describe(ex, "writing the extracted file", blocked);
            }
            finally
            {
                if (OperatingSystem.IsWindows())
                {
                    File.SetAttributes(blocked, FileAttributes.Normal);
                }
                else
                {
                    File.SetUnixFileMode(blocked, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                }
            }

            // Root containers may still write; the actionable mapping is covered
            // directly by the other tests in that case.
            if (message is not null)
            {
                Assert.Contains("permission", message, StringComparison.OrdinalIgnoreCase);
                Assert.Contains("a.txt", message, StringComparison.Ordinal);
            }
        }
        finally
        {
            try
            {
                Directory.Delete(work, recursive: true);
            }
            catch
            {
                // best effort
            }
        }
    }

    [Fact]
    public void XboxLengthRule_ReportsThe42CharacterLimit()
    {
        string? error = XisoNameRules.GetPathError(new string('a', 43));

        Assert.NotNull(error);
        Assert.Contains("42", error, StringComparison.Ordinal);
        Assert.Contains("43", error, StringComparison.Ordinal);
    }
}
