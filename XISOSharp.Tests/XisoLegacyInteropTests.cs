using System.Diagnostics;
using System.Text;
using XISOSharp.TestDataGenerator;

namespace XISOSharp.Tests;

/// <summary>
/// Interop tests for TODO #1 against the reference implementation
/// (extract-xiso 2.7.1, checked in under <c>References/</c>): images created
/// by the native tool use the legacy linked-list table layout, exercising
/// the <c>llCompat</c> reader paths no synthetic fixture reaches. Skipped
/// when the reference binary is unavailable (non-Windows).
/// </summary>
[Collection("Sequential")]
public class XisoLegacyInteropTests : IDisposable
{
    // Resolved via TestDataLocator (BUG-TEST-006): no fragile 4x ".." literal.
    private static readonly string RepoRoot = TestDataLocator.GetSolutionRoot(AppContext.BaseDirectory)
                                              ?? Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..",
                                                  "..", ".."));

    private static readonly string ExtractXisoExe = Path.Combine(RepoRoot, "References",
        "extract-xiso-build-202505152050", "extract-xiso-Win64_Release", "artifacts", "extract-xiso.exe");

    private readonly List<string> _tempDirs = [];

    public void Dispose()
    {
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

    private static bool ReferenceAvailable() => OperatingSystem.IsWindows() && File.Exists(ExtractXisoExe);

    /// <summary>
    /// Creates a legacy-layout image with the reference tool inside
    /// <paramref name="workDir"/>; returns the image path.
    /// </summary>
    private static string CreateLegacyIso(string srcDir, string workDir, string name)
    {
        var psi = new ProcessStartInfo
        {
            FileName = ExtractXisoExe,
            WorkingDirectory = workDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add(srcDir);
        psi.ArgumentList.Add(name);

        using var proc = Process.Start(psi);
        Assert.NotNull(proc);
        var stdoutTask = proc.StandardOutput.ReadToEndAsync();
        var stderrTask = proc.StandardError.ReadToEndAsync();
        if (!proc.WaitForExit(120000))
        {
            try
            {
                proc.Kill(entireProcessTree: true);
            }
            catch
            {
                // Best effort kill after timeout.
            }

            proc.WaitForExit(5000);
            Assert.Fail("extract-xiso -c timed out and was killed.");
        }

        var stdout = stdoutTask.GetAwaiter().GetResult();
        var stderr = stderrTask.GetAwaiter().GetResult();
        Assert.True(proc.ExitCode == 0, $"extract-xiso -c failed (exit {proc.ExitCode}): {stderr}{stdout}");
        _ = stdout;

        // NOTE: extract-xiso writes the image under exactly <name> (no
        // extension) in its working directory.
        var isoPath = Path.Combine(workDir, name);
        Assert.True(File.Exists(isoPath), $"reference tool did not produce {isoPath}");
        return isoPath;
    }

    private string CreateLegacyTree()
    {
        var src = CreateTempDir("xiso_leg_src");
        File.WriteAllText(Path.Combine(src, "hello.txt"), "hello legacy\n");
        var sub = Path.Combine(src, "sub");
        Directory.CreateDirectory(sub);
        File.WriteAllText(Path.Combine(sub, "inner.txt"), "inner legacy\n");
        for (var i = 0; i < 150; i++)
            File.WriteAllText(Path.Combine(src, $"file{i:000}.txt"), $"content {i}\n");

        var workDir = CreateTempDir("xiso_leg_work");
        return CreateLegacyIso(src, workDir, "legacy");
    }

    [RequiresOracleFact(OracleKind.ExtractXiso)]
    public void Legacy_ExtractLlCompat_RoundTrips()
    {
        Assert.True(ReferenceAvailable(), "Missing reference oracle 'ExtractXiso'.");

        var isoPath = CreateLegacyTree();
        var dest = CreateTempDir("xiso_leg_dest");
        Assert.Equal(0, XisoReader.Extract(isoPath, dest, true));

        Assert.Equal("hello legacy\n", File.ReadAllText(Path.Combine(dest, "hello.txt")));
        Assert.Equal("inner legacy\n", File.ReadAllText(Path.Combine(dest, "sub", "inner.txt")));
        for (var i = 0; i < 150; i++)
            Assert.Equal($"content {i}\n", File.ReadAllText(Path.Combine(dest, $"file{i:000}.txt")));
    }

    [RequiresOracleFact(OracleKind.ExtractXiso)]
    public void Legacy_List_Succeeds()
    {
        Assert.True(ReferenceAvailable(), "Missing reference oracle 'ExtractXiso'.");

        var isoPath = CreateLegacyTree();
        Assert.Equal(0, XisoReader.List(isoPath, true));
    }

    [RequiresOracleFact(OracleKind.ExtractXiso)]
    public void Legacy_Rewrite_ThenExtract_PreservesContent()
    {
        Assert.True(ReferenceAvailable(), "Missing reference oracle 'ExtractXiso'.");

        var isoPath = CreateLegacyTree();
        var rewriteDir = CreateTempDir("xiso_leg_rw");
        Assert.Equal(0, XisoReader.Rewrite(isoPath, rewriteDir, out var rewritten));
        Assert.NotNull(rewritten);

        var dest = CreateTempDir("xiso_leg_dest");
        Assert.Equal(0, XisoReader.Extract(rewritten, dest, false));
        Assert.Equal("hello legacy\n", File.ReadAllText(Path.Combine(dest, "hello.txt")));
        Assert.Equal("inner legacy\n", File.ReadAllText(Path.Combine(dest, "sub", "inner.txt")));
    }
}
