using System.Diagnostics;

namespace XISOSharp.Tests;

/// <summary>
/// Conditional <c>Fact</c>/<c>Theory</c> attributes for xUnit v2 dynamic skipping.
/// Verified 2026-09-07 against pinned xunit 2.9.3 + xunit.runner.visualstudio 4.0.0:
/// <c>Xunit.Assert</c> has no <c>Skip</c> method, <c>SkipException</c> has only a
/// private ctor plus <c>ForSkip</c> (documented v3-only), and throwing either
/// <c>SkipException.ForSkip</c> or <c>$XunitDynamicSkip$</c> reports <c>Failed</c>,
/// not <c>Skipped</c>. The only v2 mechanism yielding genuine <c>Skipped</c> is
/// static <c>Fact(Skip=...)</c>/<c>Theory(Skip=...)</c>, so these attributes
/// evaluate the condition at discovery time and set <see cref="Xunit.FactAttribute.Skip"/>.
/// </summary>
public enum OracleKind
{
    Xdvdfs,
    ExtractXiso,
    Zarchive,
}

public enum SymlinkKind
{
    DirLink,
    FileLink,
}

internal static class SkipConditions
{
    internal static string? SolutionRoot()
    {
        try
        {
            string? dir = AppContext.BaseDirectory;
            while (dir is not null)
            {
                if (File.Exists(Path.Combine(dir, "CSharp_XISOSharp.sln")))
                {
                    return dir;
                }

                dir = Path.GetDirectoryName(dir);
            }
        }
        catch
        {
            // Discovery-time probe: treat lookup failure as missing root.
        }

        return null;
    }

    internal static string? OraclePath(OracleKind kind)
    {
        string? root = SolutionRoot();
        if (root is null)
        {
            return null;
        }

        return kind switch
        {
            OracleKind.Xdvdfs => Path.Combine(root, "References", "xdvdfs-0.8.3", "xdvdfs.exe"),
            OracleKind.ExtractXiso => Path.Combine(root, "References",
                "extract-xiso-build-202505152050", "extract-xiso-Win64_Release", "artifacts", "extract-xiso.exe"),
            OracleKind.Zarchive => Path.Combine(root, "..", "CSharp_ZARSharp", "References", "ZArchive-0.1.2",
                "zarchive.exe"),
            _ => null,
        };
    }

    internal static bool OracleAvailable(OracleKind kind)
    {
        try
        {
            if (!OperatingSystem.IsWindows())
            {
                return kind == OracleKind.Zarchive && File.Exists(OraclePath(kind));
            }

            string? path = OraclePath(kind);
            return path is not null && File.Exists(path);
        }
        catch
        {
            return false;
        }
    }

    internal static bool IsNtfsTempDrive()
    {
        try
        {
            string? root = Path.GetPathRoot(Path.GetTempPath());
            if (string.IsNullOrEmpty(root))
            {
                return false;
            }

            return string.Equals(new DriveInfo(root).DriveFormat, "NTFS", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

#if NET9_0_OR_GREATER
    private static readonly Lock SymlinkGate = new();
#else
    private static readonly object SymlinkGate = new();
#endif
    private static bool? _dirLinkCache;
    private static bool? _fileLinkCache;

    internal static bool SymlinkAvailable(SymlinkKind kind)
    {
        lock (SymlinkGate)
        {
            if (kind == SymlinkKind.DirLink && _dirLinkCache.HasValue)
            {
                return _dirLinkCache.Value;
            }

            if (kind == SymlinkKind.FileLink && _fileLinkCache.HasValue)
            {
                return _fileLinkCache.Value;
            }

            bool available = ProbeSymlink(kind);
            if (kind == SymlinkKind.DirLink)
            {
                _dirLinkCache = available;
            }
            else
            {
                _fileLinkCache = available;
            }

            return available;
        }
    }

    private static bool ProbeSymlink(SymlinkKind kind)
    {
        string probeRoot = Path.Combine(Path.GetTempPath(), $"xiso_skip_probe_{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(probeRoot);
            if (kind == SymlinkKind.DirLink)
            {
                string target = Path.Combine(probeRoot, "target");
                Directory.CreateDirectory(target);
                string link = Path.Combine(probeRoot, "link");
                try
                {
                    Directory.CreateSymbolicLink(link, target);
                    return true;
                }
                catch
                {
                    if (!OperatingSystem.IsWindows())
                    {
                        return false;
                    }

                    try
                    {
                        ProcessStartInfo psi = new()
                        {
                            FileName = "cmd.exe",
                            RedirectStandardOutput = true,
                            RedirectStandardError = true,
                            UseShellExecute = false,
                            CreateNoWindow = true,
                        };
                        psi.ArgumentList.Add("/c");
                        psi.ArgumentList.Add("mklink");
                        psi.ArgumentList.Add("/J");
                        psi.ArgumentList.Add(link);
                        psi.ArgumentList.Add(target);
                        using Process? proc = Process.Start(psi);
                        if (proc is null)
                        {
                            return false;
                        }

                        Task<string> stdoutTask = proc.StandardOutput.ReadToEndAsync();
                        Task<string> stderrTask = proc.StandardError.ReadToEndAsync();
                        bool exited = proc.WaitForExit(30000);
                        stdoutTask.GetAwaiter().GetResult();
                        stderrTask.GetAwaiter().GetResult();
                        return exited && proc.ExitCode == 0 && Directory.Exists(link);
                    }
                    catch
                    {
                        return false;
                    }
                }
            }
            else
            {
                string target = Path.Combine(probeRoot, "target.txt");
                File.WriteAllText(target, "probe");
                string link = Path.Combine(probeRoot, "link.txt");
                try
                {
                    File.CreateSymbolicLink(link, target);
                    return true;
                }
                catch
                {
                    return false;
                }
            }
        }
        catch
        {
            return false;
        }
        finally
        {
            try
            {
                if (Directory.Exists(probeRoot))
                {
                    Directory.Delete(probeRoot, true);
                }
            }
            catch
            {
                // Best-effort probe cleanup.
            }
        }
    }
}

/// <summary>Skips at discovery when the current OS is not Windows.</summary>
public sealed class WindowsOnlyFactAttribute : FactAttribute
{
    public WindowsOnlyFactAttribute()
    {
        if (!OperatingSystem.IsWindows())
        {
            Skip = "Windows-only test (UNC/drive-root semantics).";
        }
    }
}

/// <summary>Skips at discovery when the current OS is not Windows.</summary>
public sealed class WindowsOnlyTheoryAttribute : TheoryAttribute
{
    public WindowsOnlyTheoryAttribute()
    {
        if (!OperatingSystem.IsWindows())
        {
            Skip = "Windows-only test (UNC/drive-root semantics).";
        }
    }
}

/// <summary>Skips at discovery when the temp drive is not NTFS (sparse-file tests).</summary>
public sealed class RequiresNtfsFactAttribute : FactAttribute
{
    public RequiresNtfsFactAttribute()
    {
        if (!SkipConditions.IsNtfsTempDrive())
        {
            Skip = "Requires NTFS temp drive (sparse SetLength would physically allocate elsewhere).";
        }
    }
}

/// <summary>Skips at discovery when the temp drive is not NTFS (sparse-file tests).</summary>
public sealed class RequiresNtfsTheoryAttribute : TheoryAttribute
{
    public RequiresNtfsTheoryAttribute()
    {
        if (!SkipConditions.IsNtfsTempDrive())
        {
            Skip = "Requires NTFS temp drive (sparse SetLength would physically allocate elsewhere).";
        }
    }
}

/// <summary>Skips at discovery when the reference oracle binary is absent.</summary>
public sealed class RequiresOracleFactAttribute : FactAttribute
{
    public RequiresOracleFactAttribute(OracleKind kind)
    {
        if (!SkipConditions.OracleAvailable(kind))
        {
            Skip = $"Missing reference oracle '{kind}' (References/ checkout absent).";
        }
    }
}

/// <summary>Skips at discovery unless <c>XISO_UPDATE_FIXTURE=1</c> is set.</summary>
public sealed class RequiresUpdateFixtureFactAttribute : FactAttribute
{
    public RequiresUpdateFixtureFactAttribute()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("XISO_UPDATE_FIXTURE"), "1",
                StringComparison.Ordinal))
        {
            Skip = "Set XISO_UPDATE_FIXTURE=1 to run the fixture-regeneration validation.";
        }
    }
}

/// <summary>Skips at discovery when symlink creation privilege is unavailable.</summary>
public sealed class RequiresSymlinkFactAttribute : FactAttribute
{
    public RequiresSymlinkFactAttribute(SymlinkKind kind)
    {
        if (!SkipConditions.SymlinkAvailable(kind))
        {
            Skip = $"Symlink privilege unavailable for '{kind}' (nothing to harden against here).";
        }
    }
}
