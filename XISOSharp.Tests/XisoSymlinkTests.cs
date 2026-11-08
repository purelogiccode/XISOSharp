using System.Diagnostics;
using XISOSharp.Cli;
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

    private string CreateTempDir(string prefix)
    {
        string dir = Path.Combine(Path.GetTempPath(), $"{prefix}_{Guid.NewGuid():N}");
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
                ProcessStartInfo psi = new("cmd.exe", $"/c mklink /J \"{linkPath}\" \"{targetPath}\"")
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                using Process? proc = Process.Start(psi);
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
        Assert.True(RemapRule.TryParse("**:{0}", out RemapRule? rule, out _));
        return [rule!];
    }

    private static HashSet<string> ListExtractedFiles(string root)
    {
        HashSet<string> result = new(StringComparer.OrdinalIgnoreCase);
        foreach (string file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            result.Add(Path.GetRelativePath(root, file).Replace('\\', '/'));
        return result;
    }

    [RequiresSymlinkFact(SymlinkKind.DirLink)]
    public void RemapWalk_CyclicDirectoryLink_TerminatesAndSkipsLink()
    {
        string root = CreateTempDir("xiso_link");
        Directory.CreateDirectory(Path.Combine(root, "sub"));
        File.WriteAllText(Path.Combine(root, "sub", "real.txt"), "real");
        // Cyclic: sub/loop -> root. Pre-fix this looped the walker forever.
        Assert.True(TryCreateDirLink(Path.Combine(root, "sub", "loop"), root),
            "Symlink privilege unavailable for 'DirLink'.");

        IReadOnlyList<(string HostPath, string ImagePath)> pairs = RemapFilesystem.DryRunRemap(root, CatchAllRule());

        Assert.Contains(pairs, p => string.Equals(p.HostPath, "/sub/real.txt", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(pairs, p => p.HostPath.Contains("loop", StringComparison.Ordinal));
    }

    [RequiresSymlinkFact(SymlinkKind.FileLink)]
    public void RemapWalk_FileSymlink_IsFollowed()
    {
        string root = CreateTempDir("xiso_link");
        File.WriteAllText(Path.Combine(root, "orig.txt"), "data");
        Assert.True(TryCreateFileLink(Path.Combine(root, "alias.txt"), Path.Combine(root, "orig.txt")),
            "Symlink privilege unavailable for 'FileLink'.");

        IReadOnlyList<(string HostPath, string ImagePath)> pairs = RemapFilesystem.DryRunRemap(root, CatchAllRule());

        Assert.Contains(pairs, p => string.Equals(p.HostPath, "/orig.txt", StringComparison.OrdinalIgnoreCase));
        (string HostPath, string ImagePath) alias = Assert.Single(pairs,
            p => string.Equals(p.HostPath, "/alias.txt", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("/alias.txt", alias.ImagePath);
    }

    [RequiresSymlinkFact(SymlinkKind.DirLink)]
    public void BuildImage_CyclicDirectoryLink_TerminatesAndSkipsLink()
    {
        // Exercises the second (build-side) walker via the public BuildImage API.
        string root = CreateTempDir("xiso_link");
        Directory.CreateDirectory(Path.Combine(root, "sub"));
        File.WriteAllText(Path.Combine(root, "a.txt"), "hello");
        File.WriteAllText(Path.Combine(root, "sub", "b.bin"), "data");
        Assert.True(TryCreateDirLink(Path.Combine(root, "sub", "loop"), root),
            "Symlink privilege unavailable for 'DirLink'.");

        string isoPath = Path.Combine(CreateTempDir("xiso_link_out"), "remap.iso");
        int rc = RemapFilesystem.BuildImage(root, isoPath, CatchAllRule());
        Assert.Equal(0, rc);

        string dest = Path.Combine(CreateTempDir("xiso_link_ext"), "out");
        Directory.CreateDirectory(dest);
        Assert.Equal(0, XisoReader.Extract(isoPath, dest, false));
        HashSet<string> files = ListExtractedFiles(dest);
        Assert.Contains("a.txt", files);
        Assert.Contains("sub/b.bin", files);
        Assert.DoesNotContain(files, f => f.Contains("loop", StringComparison.Ordinal));
    }

    [RequiresSymlinkFact(SymlinkKind.DirLink)]
    public void Pack_CyclicDirectoryLink_TerminatesAndSkipsLink()
    {
        string root = CreateTempDir("xiso_link");
        Directory.CreateDirectory(Path.Combine(root, "sub"));
        File.WriteAllText(Path.Combine(root, "a.txt"), "hello");
        File.WriteAllText(Path.Combine(root, "sub", "b.bin"), "data");
        // Cyclic: sub/loop -> root. Pre-fix this recursed until stack overflow.
        Assert.True(TryCreateDirLink(Path.Combine(root, "sub", "loop"), root),
            "Symlink privilege unavailable for 'DirLink'.");

        string isoPath = Path.Combine(CreateTempDir("xiso_link_out"), "packed.iso");
        int rc = XisoWriter.PackFromDirectory(root, isoPath);
        Assert.Equal(0, rc);

        string dest = Path.Combine(CreateTempDir("xiso_link_ext"), "out");
        Directory.CreateDirectory(dest);
        Assert.Equal(0, XisoReader.Extract(isoPath, dest, false));
        HashSet<string> files = ListExtractedFiles(dest);
        Assert.Contains("a.txt", files);
        Assert.Contains("sub/b.bin", files);
        Assert.DoesNotContain(files, f => f.Contains("loop", StringComparison.Ordinal));
    }

    [RequiresSymlinkFact(SymlinkKind.FileLink)]
    public void Pack_FileSymlink_WarnsAndPacksTarget()
    {
        // BUG-LIB-026: a file link packs its target's bytes under the link
        // name (no link representation exists) — and warns that it did so.
        string root = CreateTempDir("xiso_link");
        File.WriteAllText(Path.Combine(root, "orig.txt"), "data");
        Assert.True(TryCreateFileLink(Path.Combine(root, "alias.txt"), Path.Combine(root, "orig.txt")),
            "Symlink privilege unavailable for 'FileLink'.");

        StringWriter capture = new();
        TextWriter saved = Logger.Error;
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

        string dest = Path.Combine(CreateTempDir("xiso_link_ext"), "out");
        Directory.CreateDirectory(dest);
        Assert.Equal(0, XisoReader.Extract(isoPath, dest, false));
        Assert.Equal("data", File.ReadAllText(Path.Combine(dest, "alias.txt")));
    }

    [RequiresSymlinkFact(SymlinkKind.FileLink)]
    public void Pack_SkipMode_OmitsFileAndDirectoryLinks()
    {
        string root = CreateTempDir("xiso_link");
        File.WriteAllText(Path.Combine(root, "orig.txt"), "data");
        Directory.CreateDirectory(Path.Combine(root, "sub"));
        File.WriteAllText(Path.Combine(root, "sub", "real.txt"), "real");
        Assert.True(TryCreateFileLink(Path.Combine(root, "alias.txt"), Path.Combine(root, "orig.txt")),
            "Symlink privilege unavailable for 'FileLink'.");
        Assert.True(TryCreateDirLink(Path.Combine(root, "linked"), Path.Combine(root, "sub")),
            "Symlink privilege unavailable for 'DirLink'.");

        string isoPath = Path.Combine(CreateTempDir("xiso_link_out"), "packed.iso");
        Assert.Equal(0, XisoWriter.PackFromDirectory(root, isoPath,
            excludePatterns: null, progressCallback: null, cancellationToken: default,
            progress: null, fileTime: 0, layout: null, symlinkMode: SymlinkMode.Skip));

        string dest = Path.Combine(CreateTempDir("xiso_link_ext"), "out");
        Directory.CreateDirectory(dest);
        Assert.Equal(0, XisoReader.Extract(isoPath, dest, false));
        HashSet<string> files = ListExtractedFiles(dest);
        Assert.Contains("orig.txt", files);
        Assert.Contains("sub/real.txt", files);
        Assert.DoesNotContain("alias.txt", files);
        Assert.DoesNotContain(files, f => f.StartsWith("linked/", StringComparison.OrdinalIgnoreCase));
    }

    [RequiresSymlinkFact(SymlinkKind.FileLink)]
    public void Pack_RejectMode_NamesTheLinkAndFails()
    {
        string root = CreateTempDir("xiso_link");
        File.WriteAllText(Path.Combine(root, "orig.txt"), "data");
        Assert.True(TryCreateFileLink(Path.Combine(root, "alias.txt"), Path.Combine(root, "orig.txt")),
            "Symlink privilege unavailable for 'FileLink'.");

        string isoPath = Path.Combine(CreateTempDir("xiso_link_out"), "packed.iso");
        InvalidDataException ex = Assert.Throws<InvalidDataException>(() =>
            XisoWriter.PackFromDirectory(root, isoPath,
                excludePatterns: null, progressCallback: null, cancellationToken: default,
                progress: null, fileTime: 0, layout: null, symlinkMode: SymlinkMode.Reject));
        Assert.Contains("alias.txt", ex.Message, StringComparison.Ordinal);
    }

    [RequiresSymlinkFact(SymlinkKind.FileLink)]
    public void Cli_CreateRejectMode_NamesTheLink()
    {
        string root = CreateTempDir("xiso_link");
        File.WriteAllText(Path.Combine(root, "orig.txt"), "data");
        Assert.True(TryCreateFileLink(Path.Combine(root, "alias.txt"), Path.Combine(root, "orig.txt")),
            "Symlink privilege unavailable for 'FileLink'.");

        string isoPath = Path.Combine(CreateTempDir("xiso_link_out"), "packed.iso");
        StringWriter capture = new();
        TextWriter savedOut = Logger.Out;
        TextWriter savedErr = Logger.Error;
        bool savedQuiet = Logger.Quiet;
        bool savedRealQuiet = Logger.RealQuiet;
        Logger.Out = capture;
        Logger.Error = capture;
        Logger.Quiet = false;
        Logger.RealQuiet = false;
        int rc;
        try
        {
            rc = Program.Main(["-c", root, isoPath, "--symlink-mode", "reject"]);
        }
        finally
        {
            Logger.Out = savedOut;
            Logger.Error = savedErr;
            Logger.Quiet = savedQuiet;
            Logger.RealQuiet = savedRealQuiet;
        }

        Assert.Equal(1, rc);
        Assert.Contains("alias.txt", capture.ToString(), StringComparison.Ordinal);
        Assert.False(File.Exists(isoPath));
    }

    [RequiresSymlinkFact(SymlinkKind.FileLink)]
    public void BuildImage_RejectMode_NamesTheLinkAndFails()
    {
        string root = CreateTempDir("xiso_link");
        File.WriteAllText(Path.Combine(root, "orig.txt"), "data");
        Assert.True(TryCreateFileLink(Path.Combine(root, "alias.txt"), Path.Combine(root, "orig.txt")),
            "Symlink privilege unavailable for 'FileLink'.");

        string isoPath = Path.Combine(CreateTempDir("xiso_link_out"), "remap.iso");
        StringWriter capture = new();
        TextWriter saved = Logger.Error;
        Logger.Error = capture;
        int rc;
        try
        {
            rc = RemapFilesystem.BuildImage(root, isoPath, CatchAllRule(), progress: null, ct: default,
                fileTime: 0, layout: null, symlinkMode: SymlinkMode.Reject);
        }
        finally
        {
            Logger.Error = saved;
        }

        Assert.Equal(1, rc);
        Assert.Contains("alias.txt", capture.ToString(), StringComparison.Ordinal);
    }

    [RequiresSymlinkFact(SymlinkKind.FileLink)]
    public void Apply_RejectMode_NamesTheLinkAndFails()
    {
        string source = CreateTempDir("xiso_link_apply_src");
        File.WriteAllText(Path.Combine(source, "orig.txt"), "data");
        string iso = Path.Combine(CreateTempDir("xiso_link_apply_iso"), "game.iso");
        Assert.Equal(0, XisoWriter.PackFromDirectory(source, iso, fileTime: 0));

        string skeleton = Path.Combine(CreateTempDir("xiso_link_apply_skel"), "game.skeleton.xiso");
        Assert.True(XisoSkeleton.Petrify(iso, skeleton, quiet: true));

        Assert.True(TryCreateFileLink(Path.Combine(source, "alias.txt"), Path.Combine(source, "orig.txt")),
            "Symlink privilege unavailable for 'FileLink'.");

        string output = Path.Combine(CreateTempDir("xiso_link_apply_out"), "applied.iso");
        InvalidDataException ex = Assert.Throws<InvalidDataException>(() =>
            XisoSkeleton.Apply(skeleton, source, output, hashPath: null, cancellationToken: default,
                strictNames: false, symlinkMode: SymlinkMode.Reject));
        Assert.Contains("alias.txt", ex.Message, StringComparison.Ordinal);
    }
}
