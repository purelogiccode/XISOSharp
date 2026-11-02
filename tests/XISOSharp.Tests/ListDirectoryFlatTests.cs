using XISOSharp.Models;

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

        foreach (string dir in _tempDirs)
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
        string dir = Path.Combine(Path.GetTempPath(), $"xiso_ls_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        _tempDirs.Add(dir);
        return dir;
    }

    private string CreateSourceTree()
    {
        string root = Path.Combine(Path.GetTempPath(), $"xiso_ls_src_{Guid.NewGuid():N}");
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
        string outputDir = CreateTempDir();

        int result = XisoWriter.CreateXiso(srcDir, outputDir, null, null, out string? isoPath, null, null);
        Assert.Equal(0, result);
        Assert.NotNull(isoPath);
        return isoPath;
    }

    [Fact]
    public void ListDirectoryFlat_Root_ReturnsTopLevelNames()
    {
        string isoPath = CreateIso(CreateSourceTree());

        IReadOnlyList<string> names = XisoReader.ListDirectoryFlat(isoPath);

        Assert.Equal(3, names.Count); // default.xbe, media, empty
        Assert.Contains("default.xbe", names, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("media", names, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("empty", names, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void ListDirectoryFlat_Subdirectory_DoesNotRecurse()
    {
        string isoPath = CreateIso(CreateSourceTree());

        IReadOnlyList<string> names = XisoReader.ListDirectoryFlat(isoPath, "/media");

        Assert.Equal(2, names.Count); // video.bik, sub
        Assert.Contains("video.bik", names, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("sub", names, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("deep.txt", names, StringComparer.OrdinalIgnoreCase); // nested entry not listed
    }

    [Fact]
    public void ListDirectoryFlat_EmptyDirectory_ReturnsEmpty()
    {
        string isoPath = CreateIso(CreateSourceTree());

        IReadOnlyList<string> names = XisoReader.ListDirectoryFlat(isoPath, "/empty");

        Assert.Empty(names);
    }

    [Fact]
    public void ListDirectoryFlat_MissingPath_Throws()
    {
        string isoPath = CreateIso(CreateSourceTree());

        Assert.Throws<InvalidDataException>(() => XisoReader.ListDirectoryFlat(isoPath, "/nope"));
    }

    [Fact]
    public void ListDirectoryFlat_MissingFile_Throws() =>
        Assert.Throws<FileNotFoundException>(() => XisoReader.ListDirectoryFlat("no_such_file.iso"));

    [Fact]
    public void ListDirectoryFlat_InvalidIso_Throws()
    {
        string junk = CreateTempDir();
        string junkFile = Path.Combine(junk, "junk.iso");
        File.WriteAllBytes(junkFile, new byte[4096]);

        Assert.Throws<XisoFormatException>(() => XisoReader.ListDirectoryFlat(junkFile));
    }

    [Fact]
    public void ListDirectoryFlat_MatchesListDirectoryNames()
    {
        string isoPath = CreateIso(CreateSourceTree());

        IReadOnlyList<string> flat = XisoReader.ListDirectoryFlat(isoPath, "/media");
        IReadOnlyList<EntryInfo> entries = XisoReader.ListDirectory(isoPath, "/media");

        Assert.Equal(entries.Select(e => e.Name).OrderBy(n => n, StringComparer.Ordinal),
            flat.OrderBy(n => n, StringComparer.Ordinal));
    }
}
