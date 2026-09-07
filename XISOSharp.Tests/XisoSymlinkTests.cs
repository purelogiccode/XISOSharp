using System.Diagnostics;
using XISOSharp.Models;

namespace XISOSharp.Tests;

/// <summary>
/// Symlink/junction hardening (TODO #21): directory reparse points are skipped,
/// never descended — a cyclic link must terminate the walk, not hang it.
/// Symlinks to files are still followed (target content is packed).
/// Pack hops CWD, so this runs in the Sequential collection.
/// </summary>
[Collection("Sequential")]
public class XisoSymlinkTests : IDisposable
{
    private readonly List<string> _tempDirs = [];
    private readonly bool _savedQuiet;
    private readonly bool _savedRealQuiet;

    public XisoSymlinkTests()
    {
        _savedQuiet = Logger.Quiet;
        _savedRealQuiet = Logger.RealQuiet;
    }

    public void Dispose()
    {
        Logger.Quiet = _savedQuiet;
        Logger.RealQuiet = _savedRealQuiet;
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

    private string CreateTempDir(string prefix)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"{prefix}_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        _tempDirs.Add(dir);
        return dir;
    }

    /// <summary>
    /// Creates a directory link. Falls back to <c>mklink /J</c> on Windows when
    /// symlink privilege is missing (junctions need none). Returns false when
    /// the OS refuses; callers return early (nothing to harden against here).
    /// </summary>
    private static bool TryCreateDirLink(string linkPath, string targetPath)
    {
        try
        {
            Directory.CreateSymbolicLink(linkPath, targetPath);
            return true;
        }
        catch
        {
            if (!OperatingSystem.IsWindows())
                return false;
            try
            {
                var psi = new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{linkPath}\" \"{targetPath}\"")
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                using var proc = Process.Start(psi);
                if (proc == null)
                    return false;
                proc.WaitForExit(30000);
                return proc.ExitCode == 0 && Directory.Exists(linkPath);
            }
            catch
            {
                return false;
            }
        }
    }

    private static bool TryCreateFileLink(string linkPath, string targetPath)
    {
        try
        {
            File.CreateSymbolicLink(linkPath, targetPath);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static List<RemapRule> CatchAllRule()
    {
        Assert.True(RemapRule.TryParse("**:{0}", out var rule, out _));
        return [rule!];
    }

    private static HashSet<string> ListExtractedFiles(string root)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            result.Add(Path.GetRelativePath(root, file).Replace('\\', '/'));
        return result;
    }

    [RequiresSymlinkFact(SymlinkKind.DirLink)]
    public void RemapWalk_CyclicDirectoryLink_TerminatesAndSkipsLink()
    {
        var root = CreateTempDir("xiso_link");
        Directory.CreateDirectory(Path.Combine(root, "sub"));
        File.WriteAllText(Path.Combine(root, "sub", "real.txt"), "real");
        // Cyclic: sub/loop -> root. Pre-fix this looped the walker forever.
        Assert.True(TryCreateDirLink(Path.Combine(root, "sub", "loop"), root),
            "Symlink privilege unavailable for 'DirLink'.");

        var pairs = RemapFilesystem.DryRunRemap(root, CatchAllRule());

        Assert.Contains(pairs, p => string.Equals(p.HostPath, "/sub/real.txt", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(pairs, p => p.HostPath.Contains("loop", StringComparison.Ordinal));
    }

    [RequiresSymlinkFact(SymlinkKind.FileLink)]
    public void RemapWalk_FileSymlink_IsFollowed()
    {
        var root = CreateTempDir("xiso_link");
        File.WriteAllText(Path.Combine(root, "orig.txt"), "data");
        Assert.True(TryCreateFileLink(Path.Combine(root, "alias.txt"), Path.Combine(root, "orig.txt")),
            "Symlink privilege unavailable for 'FileLink'.");

        var pairs = RemapFilesystem.DryRunRemap(root, CatchAllRule());

        Assert.Contains(pairs, p => string.Equals(p.HostPath, "/orig.txt", StringComparison.OrdinalIgnoreCase));
        var alias = Assert.Single(pairs,
            p => string.Equals(p.HostPath, "/alias.txt", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("/alias.txt", alias.ImagePath);
    }

    [RequiresSymlinkFact(SymlinkKind.DirLink)]
    public void BuildImage_CyclicDirectoryLink_TerminatesAndSkipsLink()
    {
        // Exercises the second (build-side) walker via the public BuildImage API.
        var root = CreateTempDir("xiso_link");
        Directory.CreateDirectory(Path.Combine(root, "sub"));
        File.WriteAllText(Path.Combine(root, "a.txt"), "hello");
        File.WriteAllText(Path.Combine(root, "sub", "b.bin"), "data");
        Assert.True(TryCreateDirLink(Path.Combine(root, "sub", "loop"), root),
            "Symlink privilege unavailable for 'DirLink'.");

        var isoPath = Path.Combine(CreateTempDir("xiso_link_out"), "remap.iso");
        var rc = RemapFilesystem.BuildImage(root, isoPath, CatchAllRule());
        Assert.Equal(0, rc);

        var dest = Path.Combine(CreateTempDir("xiso_link_ext"), "out");
        Directory.CreateDirectory(dest);
        Assert.Equal(0, XisoReader.Extract(isoPath, dest, false));
        var files = ListExtractedFiles(dest);
        Assert.Contains("a.txt", files);
        Assert.Contains("sub/b.bin", files);
        Assert.DoesNotContain(files, f => f.Contains("loop", StringComparison.Ordinal));
    }

    [RequiresSymlinkFact(SymlinkKind.DirLink)]
    public void Pack_CyclicDirectoryLink_TerminatesAndSkipsLink()
    {
        var root = CreateTempDir("xiso_link");
        Directory.CreateDirectory(Path.Combine(root, "sub"));
        File.WriteAllText(Path.Combine(root, "a.txt"), "hello");
        File.WriteAllText(Path.Combine(root, "sub", "b.bin"), "data");
        // Cyclic: sub/loop -> root. Pre-fix this recursed until stack overflow.
        Assert.True(TryCreateDirLink(Path.Combine(root, "sub", "loop"), root),
            "Symlink privilege unavailable for 'DirLink'.");

        var isoPath = Path.Combine(CreateTempDir("xiso_link_out"), "packed.iso");
        var rc = XisoWriter.PackFromDirectory(root, isoPath);
        Assert.Equal(0, rc);

        var dest = Path.Combine(CreateTempDir("xiso_link_ext"), "out");
        Directory.CreateDirectory(dest);
        Assert.Equal(0, XisoReader.Extract(isoPath, dest, false));
        var files = ListExtractedFiles(dest);
        Assert.Contains("a.txt", files);
        Assert.Contains("sub/b.bin", files);
        Assert.DoesNotContain(files, f => f.Contains("loop", StringComparison.Ordinal));
    }

    [RequiresSymlinkFact(SymlinkKind.FileLink)]
    public void Pack_FileSymlink_WarnsAndPacksTarget()
    {
        // BUG-LIB-026: a file link packs its target's bytes under the link
        // name (no link representation exists) — and warns that it did so.
        var root = CreateTempDir("xiso_link");
        File.WriteAllText(Path.Combine(root, "orig.txt"), "data");
        Assert.True(TryCreateFileLink(Path.Combine(root, "alias.txt"), Path.Combine(root, "orig.txt")),
            "Symlink privilege unavailable for 'FileLink'.");

        var capture = new StringWriter();
        var saved = Logger.Error;
        Logger.Error = capture;
        string isoPath;
        try
        {
            isoPath = Path.Combine(CreateTempDir("xiso_link_out"), "packed.iso");
            Assert.Equal(0, XisoWriter.PackFromDirectory(root, isoPath));
        }
        finally
        {
            Logger.Error = saved;
        }

        Assert.Contains("packing symlink", capture.ToString(), StringComparison.OrdinalIgnoreCase);

        var dest = Path.Combine(CreateTempDir("xiso_link_ext"), "out");
        Directory.CreateDirectory(dest);
        Assert.Equal(0, XisoReader.Extract(isoPath, dest, false));
        Assert.Equal("data", File.ReadAllText(Path.Combine(dest, "alias.txt")));
    }
}