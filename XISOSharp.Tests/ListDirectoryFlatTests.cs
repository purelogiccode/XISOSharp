namespace XISOSharp.Tests;

/// <summary>
/// Tests for <see cref="XisoReader.ListDirectoryFlat"/> — non-recursive name listing
/// of a directory within an XISO image (the library behind the CLI's --ls flag).
/// </summary>
[Collection("Sequential")]
public class ListDirectoryFlatTests : IDisposable
{
    private readonly List<string> _tempDirs = [];

    public void Dispose()
    {
        Logger.Quiet = false;
        Logger.RealQuiet = false;

        foreach (var dir in _tempDirs)
        {
            try
            {
                if (Directory.Exists(dir)) Directory.Delete(dir, true);
            }
            catch
            {
                /* best effort cleanup */
            }
        }
    }

    private string CreateTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"xiso_ls_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        _tempDirs.Add(dir);
        return dir;
    }

    private string CreateSourceTree()
    {
        var root = Path.Combine(Path.GetTempPath(), $"xiso_ls_src_{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(root, "media", "sub"));
        Directory.CreateDirectory(Path.Combine(root, "empty"));

        File.WriteAllText(Path.Combine(root, "default.xbe"), "xbe");
        File.WriteAllText(Path.Combine(root, "media", "video.bik"), "video");
        File.WriteAllText(Path.Combine(root, "media", "sub", "deep.txt"), "deep");
        _tempDirs.Add(root);
        return root;
    }

    private string CreateIso(string srcDir)
    {
        var outputDir = CreateTempDir();

        var result = XisoWriter.CreateXiso(srcDir, outputDir, null, null, out var isoPath, null, null);
        Assert.Equal(0, result);
        Assert.NotNull(isoPath);
        return isoPath;
    }

    [Fact]
    public void ListDirectoryFlat_Root_ReturnsTopLevelNames()
    {
        var isoPath = CreateIso(CreateSourceTree());

        var names = XisoReader.ListDirectoryFlat(isoPath);

        Assert.Equal(3, names.Count); // default.xbe, media, empty
        Assert.Contains("default.xbe", names, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("media", names, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("empty", names, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void ListDirectoryFlat_Subdirectory_DoesNotRecurse()
    {
        var isoPath = CreateIso(CreateSourceTree());

        var names = XisoReader.ListDirectoryFlat(isoPath, "/media");

        Assert.Equal(2, names.Count); // video.bik, sub
        Assert.Contains("video.bik", names, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("sub", names, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("deep.txt", names, StringComparer.OrdinalIgnoreCase); // nested entry not listed
    }

    [Fact]
    public void ListDirectoryFlat_EmptyDirectory_ReturnsEmpty()
    {
        var isoPath = CreateIso(CreateSourceTree());

        var names = XisoReader.ListDirectoryFlat(isoPath, "/empty");

        Assert.Empty(names);
    }

    [Fact]
    public void ListDirectoryFlat_MissingPath_Throws()
    {
        var isoPath = CreateIso(CreateSourceTree());

        Assert.Throws<InvalidDataException>(() => XisoReader.ListDirectoryFlat(isoPath, "/nope"));
    }

    [Fact]
    public void ListDirectoryFlat_MissingFile_Throws()
    {
        Assert.Throws<FileNotFoundException>(() => XisoReader.ListDirectoryFlat("no_such_file.iso"));
    }

    [Fact]
    public void ListDirectoryFlat_InvalidIso_Throws()
    {
        var junk = CreateTempDir();
        var junkFile = Path.Combine(junk, "junk.iso");
        File.WriteAllBytes(junkFile, new byte[4096]);

        Assert.Throws<XisoFormatException>(() => XisoReader.ListDirectoryFlat(junkFile));
    }

    [Fact]
    public void ListDirectoryFlat_MatchesListDirectoryNames()
    {
        var isoPath = CreateIso(CreateSourceTree());

        var flat = XisoReader.ListDirectoryFlat(isoPath, "/media");
        var entries = XisoReader.ListDirectory(isoPath, "/media");

        Assert.Equal(entries.Select(e => e.Name).OrderBy(n => n, StringComparer.Ordinal),
            flat.OrderBy(n => n, StringComparer.Ordinal));
    }
}