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

    [Fact]
    public void RemapWalk_CyclicDirectoryLink_TerminatesAndSkipsLink()
    {
        var root = CreateTempDir("xiso_link");
        Directory.CreateDirectory(Path.Combine(root, "sub"));
        File.WriteAllText(Path.Combine(root, "sub", "real.txt"), "real");
        // Cyclic: sub/loop -> root. Pre-fix this looped the walker forever.
        if (!TryCreateDirLink(Path.Combine(root, "sub", "loop"), root))
            return;

        var pairs = RemapFilesystem.DryRunRemap(root, CatchAllRule());

        Assert.Contains(pairs, p => string.Equals(p.HostPath, "/sub/real.txt", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(pairs, p => p.HostPath.Contains("loop", StringComparison.Ordinal));
    }

    [Fact]
    public void RemapWalk_FileSymlink_IsFollowed()
    {
        var root = CreateTempDir("xiso_link");
        File.WriteAllText(Path.Combine(root, "orig.txt"), "data");
        if (!TryCreateFileLink(Path.Combine(root, "alias.txt"), Path.Combine(root, "orig.txt")))
            return;

        var pairs = RemapFilesystem.DryRunRemap(root, CatchAllRule());

        Assert.Contains(pairs, p => string.Equals(p.HostPath, "/orig.txt", StringComparison.OrdinalIgnoreCase));
        var alias = Assert.Single(pairs,
            p => string.Equals(p.HostPath, "/alias.txt", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("/alias.txt", alias.ImagePath);
    }

    [Fact]
    public void BuildImage_CyclicDirectoryLink_TerminatesAndSkipsLink()
    {
        // Exercises the second (build-side) walker via the public BuildImage API.
        var root = CreateTempDir("xiso_link");
        Directory.CreateDirectory(Path.Combine(root, "sub"));
        File.WriteAllText(Path.Combine(root, "a.txt"), "hello");
        File.WriteAllText(Path.Combine(root, "sub", "b.bin"), "data");
        if (!TryCreateDirLink(Path.Combine(root, "sub", "loop"), root))
            return;

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

    [Fact]
    public void Pack_CyclicDirectoryLink_TerminatesAndSkipsLink()
    {
        var root = CreateTempDir("xiso_link");
        Directory.CreateDirectory(Path.Combine(root, "sub"));
        File.WriteAllText(Path.Combine(root, "a.txt"), "hello");
        File.WriteAllText(Path.Combine(root, "sub", "b.bin"), "data");
        // Cyclic: sub/loop -> root. Pre-fix this recursed until stack overflow.
        if (!TryCreateDirLink(Path.Combine(root, "sub", "loop"), root))
            return;

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
}