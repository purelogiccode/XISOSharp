using System.Text;

namespace XISOSharp.Tests;

/// <summary>
/// Tests for <see cref="RemapRule"/> and <see cref="RemapFilesystem"/>.
/// Covers rule parsing, spec file handling, dry-run and image building.
/// </summary>
[Collection("Sequential")]
public class RemapFilesystemTests : IDisposable
{
    private readonly List<string> _tempDirs = [];
    private readonly List<string> _tempFiles = [];

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
                // ignored
            }
        }

        foreach (var file in _tempFiles)
        {
            try
            {
                if (File.Exists(file)) File.Delete(file);
            }
            catch
            {
                // ignored
            }
        }
    }

    private string CreateTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"xiso_remap_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        _tempDirs.Add(dir);
        return dir;
    }

    private static void CreateFile(string root, string relative, string content = "data")
    {
        var full = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    // -----------------------------------------------------------------
    // RemapRule.TryParse
    // -----------------------------------------------------------------

    [Fact]
    public void TryParse_ValidSimpleRule_Succeeds()
    {
        bool ok = RemapRule.TryParse("src/**:dest/{1}", out var rule, out var error);
        Assert.True(ok);
        Assert.NotNull(rule);
        Assert.Null(error);
        Assert.Equal("src/**", rule.HostGlob);
        Assert.Equal("dest/{1}", rule.ImagePath);
        Assert.False(rule.IsExclusion);
    }

    [Fact]
    public void TryParse_ValidSingleFileRule_Succeeds()
    {
        bool ok = RemapRule.TryParse("*.txt:docs/{1}", out var rule, out _);
        Assert.True(ok);
        Assert.NotNull(rule);
        Assert.Equal("*.txt", rule.HostGlob);
        Assert.Equal("docs/{1}", rule.ImagePath);
    }

    [Fact]
    public void TryParse_ExclusionRule_SetsIsExclusion()
    {
        bool ok = RemapRule.TryParse("!skip/**", out var rule, out var error);
        Assert.True(ok);
        Assert.NotNull(rule);
        Assert.True(rule.IsExclusion);
        Assert.Equal("skip/**", rule.HostGlob);
        Assert.Equal(string.Empty, rule.ImagePath);
        Assert.Null(error);
    }

    [Fact]
    public void TryParse_ExclusionWithImagePath_Succeeds()
    {
        // Exclusion may still have image part but it is ignored; host is !pattern
        bool ok = RemapRule.TryParse("!skip/**:ignored", out var rule, out _);
        Assert.True(ok);
        Assert.NotNull(rule);
        Assert.True(rule.IsExclusion);
    }

    [Fact]
    public void TryParse_EmptyRaw_Fails()
    {
        bool ok = RemapRule.TryParse("", out var rule, out var error);
        Assert.False(ok);
        Assert.Null(rule);
        Assert.NotNull(error);
        Assert.Contains("empty", error, StringComparison.OrdinalIgnoreCase);

        ok = RemapRule.TryParse("   ", out rule, out error);
        Assert.False(ok);
        Assert.NotNull(error);
    }

    [Fact]
    public void TryParse_MissingImagePath_ForNonExclusion_Fails()
    {
        bool ok = RemapRule.TryParse("src/**", out var rule, out var error);
        Assert.False(ok);
        Assert.Null(rule);
        Assert.NotNull(error);
        Assert.Contains("image path", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TryParse_EmptyHost_Fails()
    {
        bool ok = RemapRule.TryParse(":dest", out var rule, out var error);
        Assert.False(ok);
        Assert.Null(rule);
        Assert.NotNull(error);
    }

    [Fact]
    public void TryParse_ExclusionEmptyHost_Fails()
    {
        Assert.False(RemapRule.TryParse("! :dest", out _, out var error));
        Assert.NotNull(error);
        // Try another: "!"
        bool ok2 = RemapRule.TryParse("!", out _, out var err2);
        Assert.False(ok2);
        Assert.NotNull(err2);
    }

    [Fact]
    public void TryParse_InvalidHostGlob_EmbeddedDoubleStar_Fails()
    {
        bool ok = RemapRule.TryParse("a**/b:dest", out var rule, out var error);
        Assert.False(ok);
        Assert.Null(rule);
        Assert.NotNull(error);
        Assert.Contains("Invalid host glob", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TryParse_InvalidRewrite_NonDigitInBraces_Fails()
    {
        bool ok = RemapRule.TryParse("src/**:dest/{a}", out var rule, out var error);
        Assert.False(ok);
        Assert.Null(rule);
        Assert.NotNull(error);
        Assert.Contains("Invalid rewrite", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TryParse_InvalidRewrite_NestedBrace_Fails()
    {
        bool ok = RemapRule.TryParse("src/**:dest/{{1}", out var rule, out var error);
        Assert.False(ok);
        Assert.Null(rule);
        Assert.NotNull(error);
    }

    [Fact]
    public void TryParse_InvalidRewrite_UnclosedBrace_Fails()
    {
        bool ok = RemapRule.TryParse("src/**:dest/{1", out var rule, out var error);
        Assert.False(ok);
        Assert.Null(rule);
        Assert.NotNull(error);
    }

    // -----------------------------------------------------------------
    // ParseSpecText / ParseSpecFile / GenerateSpecText
    // -----------------------------------------------------------------

    [Fact]
    public void ParseSpecText_WithMetadataAndRules_ParsesCorrectly()
    {
        string toml = """
                      [metadata]
                      output = "out.iso"

                      [map_rules]
                      "src/**" = "dest/{1}"
                      "!skip/**" = ""
                      "*.txt" = "docs/{0}"
                      """;

        (string? output, List<RemapRule> rules) = RemapFilesystem.ParseSpecText(toml);
        Assert.Equal("out.iso", output);
        Assert.Equal(3, rules.Count);
        Assert.Equal("src/**", rules[0].HostGlob);
        Assert.Equal("dest/{1}", rules[0].ImagePath);
        Assert.False(rules[0].IsExclusion);
        Assert.True(rules[1].IsExclusion);
        Assert.Equal("skip/**", rules[1].HostGlob);
        Assert.Equal("*.txt", rules[2].HostGlob);
    }

    [Fact]
    public void ParseSpecText_IgnoresCommentsAndEmptyLines()
    {
        string toml = """
                      # comment
                      [metadata]
                      # another
                      output = "a.iso"

                      [map_rules]
                      ; semicolon comment
                      "a/**" = "b/{1}"

                      """;
        (string? output, List<RemapRule> rules) = RemapFilesystem.ParseSpecText(toml);
        Assert.Equal("a.iso", output);
        Assert.Single(rules);
    }

    [Fact]
    public void ParseSpecFile_ReadsFromFile()
    {
        string toml = """
                      [metadata]
                      output = "fromfile.iso"
                      [map_rules]
                      "**" = "{0}"
                      """;
        var tmp = Path.Combine(Path.GetTempPath(), $"xiso_spec_{Guid.NewGuid():N}.toml");
        File.WriteAllText(tmp, toml, Encoding.UTF8);
        _tempFiles.Add(tmp);

        (string? output, List<RemapRule> rules) = RemapFilesystem.ParseSpecFile(tmp);
        Assert.Equal("fromfile.iso", output);
        Assert.Single(rules);
        Assert.Equal("**", rules[0].HostGlob);
    }

    [Fact]
    public void ParseSpecFile_MissingFile_Throws()
    {
        var missing = Path.Combine(Path.GetTempPath(), $"missing_{Guid.NewGuid():N}.toml");
        Assert.Throws<FileNotFoundException>(() => RemapFilesystem.ParseSpecFile(missing));
    }

    [Fact]
    public void GenerateSpecText_RoundTrip_PreservesRules()
    {
        var rules = new List<RemapRule>
        {
            new() { HostGlob = "src/**", ImagePath = "dest/{1}", IsExclusion = false },
            new() { HostGlob = "skip/**", ImagePath = "", IsExclusion = true },
        };
        string toml = RemapFilesystem.GenerateSpecText(rules, "out.iso");
        Assert.Contains("output = \"out.iso\"", toml, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("src/**", toml, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("!skip/**", toml, StringComparison.OrdinalIgnoreCase);

        (string? output, List<RemapRule> parsed) = RemapFilesystem.ParseSpecText(toml);
        Assert.Equal("out.iso", output);
        Assert.Equal(2, parsed.Count);
        Assert.Equal(rules[0].HostGlob, parsed[0].HostGlob);
        Assert.Equal(rules[0].ImagePath, parsed[0].ImagePath);
        Assert.Equal(rules[0].IsExclusion, parsed[0].IsExclusion);
        Assert.Equal(rules[1].IsExclusion, parsed[1].IsExclusion);
    }

    [Fact]
    public void GenerateSpecText_WithoutOutput_OmitsMetadata()
    {
        var rules = new List<RemapRule> { new() { HostGlob = "**", ImagePath = "{0}" } };
        string toml = RemapFilesystem.GenerateSpecText(rules, null);
        Assert.DoesNotContain("[metadata]", toml, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("[map_rules]", toml, StringComparison.OrdinalIgnoreCase);
    }

    // -----------------------------------------------------------------
    // DryRunRemap
    // -----------------------------------------------------------------

    [Fact]
    public void DryRunRemap_SimpleWildcard_MapsFiles()
    {
        var src = CreateTempDir();
        CreateFile(src, "a.txt", "a");
        CreateFile(src, "b.txt", "b");
        CreateFile(src, "sub/c.txt", "c");

        // Map all txt files to docs/
        Assert.True(RemapRule.TryParse("**/*.txt:docs/{0}", out _, out _));
        // Actually "**/*.txt" captures via WaxGlob? For remap, {0} is whole match, should produce docs/<path>. DryRun will rewrite via caps[0].
        // Simpler: map everything to root via "**"
        Assert.True(RemapRule.TryParse("**:{0}", out var rAll, out _));
        var rules = new List<RemapRule> { rAll! };

        var mappings = RemapFilesystem.DryRunRemap(src, rules);
        // Every file should be mapped to itself (since {0} is whole)
        Assert.Contains(mappings,
            m => string.Equals(m.HostPath, "/a.txt", StringComparison.OrdinalIgnoreCase) &&
                 string.Equals(m.ImagePath, "/a.txt", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(mappings,
            m => string.Equals(m.HostPath, "/sub/c.txt", StringComparison.OrdinalIgnoreCase) &&
                 string.Equals(m.ImagePath, "/sub/c.txt", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void DryRunRemap_WithCapture_RemapsToDest()
    {
        var src = CreateTempDir();
        CreateFile(src, "src/file.txt", "hello");
        CreateFile(src, "src/sub/nested.txt", "world");
        CreateFile(src, "other.txt", "other");

        Assert.True(RemapRule.TryParse("src/**:dest/{1}", out var rule, out _));
        var rules = new List<RemapRule> { rule! };

        var mappings = RemapFilesystem.DryRunRemap(src, rules);
        // src/* should be mapped to dest/*
        Assert.Contains(mappings,
            m => string.Equals(m.HostPath, "/src/file.txt", StringComparison.OrdinalIgnoreCase) &&
                 string.Equals(m.ImagePath, "/dest/file.txt", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(mappings,
            m => string.Equals(m.HostPath, "/src/sub/nested.txt", StringComparison.OrdinalIgnoreCase) &&
                 string.Equals(m.ImagePath, "/dest/sub/nested.txt", StringComparison.OrdinalIgnoreCase));
        // other.txt is child of ? It is not under src, and src/** does not match other.txt, so it should not be mapped.
        // But due to parentPrefix logic, only descendants of matched prefix are mapped. other.txt has no prefix, so omitted.
        Assert.DoesNotContain(mappings,
            m => string.Equals(m.HostPath, "/other.txt", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void DryRunRemap_Exclusion_RemovesMatchingFiles()
    {
        var src = CreateTempDir();
        CreateFile(src, "keep.txt", "keep");
        CreateFile(src, "skip.txt", "skip");
        CreateFile(src, "sub/keep2.txt", "keep2");
        CreateFile(src, "sub/skip2.txt", "skip2");

        Assert.True(RemapRule.TryParse("**:{0}", out var all, out _));
        Assert.True(RemapRule.TryParse("!**/skip*.txt", out var excl, out _));
        // Order matters: exclusion after inclusion should exclude.
        // But BuildMappings processes rules in order per prefix: first match via any glob, then iterates ordered rules F
        // For DryRun, exclusion should null out rewritten if later rule is exclusion but IsExclusion clears.
        // Let's test with mapping all then excluding skip
        var rulesInclusionFirst = new List<RemapRule> { all!, excl! };
        var mappings = RemapFilesystem.DryRunRemap(src, rulesInclusionFirst);
        // Depending on implementation, exclusion after inclusion with same prefix should exclude?
        // The logic: for each prefix, loop rules idx 0..count-1, if caps matches and IsExclusion -> rewritten=null; continue;
        // if already rewritten != null -> continue (skip)
        // So exclusion only applies if it appears before the inclusion that would set rewritten, or it clears previous?
        // Actually rewritten=null continue after exclusion, but if rewritten was already set, exclusion still clears it (since it doesn't check rewritten != null before clearing).
        // For rules [all, excl]: for prefix "skip.txt": idx0 all caps not null, IsExclusion false, rewritten=null so it sets rewritten to "skip.txt"; idx1 excl caps not null, IsExclusion true => rewritten=null (clears). So final null -> omitted.
        // For order [excl, all]: for "skip.txt": idx0 excl matches -> rewritten stays null; idx1 all matches but IsExclusion false, but rewritten==null so it would set. So order matters differently.
        // We test inclusion then exclusion should exclude.
        Assert.DoesNotContain(mappings, m => m.HostPath.Contains("skip", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(mappings, m => string.Equals(m.HostPath, "/keep.txt", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(mappings, m => string.Equals(m.HostPath, "/sub/keep2.txt", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void DryRunRemap_DirectoryMapping_MapsChildrenViaSuffix()
    {
        var src = CreateTempDir();
        CreateFile(src, "srcdir/file.txt", "a");
        CreateFile(src, "srcdir/sub/b.txt", "b");

        // Map directory srcdir to destdir (no wildcard)
        Assert.True(RemapRule.TryParse("srcdir:destdir", out var rule, out _));
        var mappings = RemapFilesystem.DryRunRemap(src, new List<RemapRule> { rule! });

        Assert.Contains(mappings,
            m => string.Equals(m.HostPath, "/srcdir/file.txt", StringComparison.OrdinalIgnoreCase) &&
                 string.Equals(m.ImagePath, "/destdir/file.txt", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(mappings,
            m => string.Equals(m.HostPath, "/srcdir/sub/b.txt", StringComparison.OrdinalIgnoreCase) &&
                 string.Equals(m.ImagePath, "/destdir/sub/b.txt", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void DryRunRemap_MissingSourceDir_Throws()
    {
        var missing = Path.Combine(Path.GetTempPath(), $"missing_{Guid.NewGuid():N}");
        Assert.True(RemapRule.TryParse("**:{0}", out var r, out _));
        Assert.Throws<DirectoryNotFoundException>(() =>
            RemapFilesystem.DryRunRemap(missing, new List<RemapRule> { r! }));
    }

    [Fact]
    public void DryRunRemap_EmptyRules_MapsNothing()
    {
        var src = CreateTempDir();
        CreateFile(src, "a.txt", "x");
        var mappings = RemapFilesystem.DryRunRemap(src, new List<RemapRule>());
        Assert.Empty(mappings);
    }

    [Fact]
    public void DryRunRemap_FirstWins_ForDuplicateGuest()
    {
        var src = CreateTempDir();
        CreateFile(src, "a.txt", "contentA");
        CreateFile(src, "b.txt", "contentB");

        Assert.True(RemapRule.TryParse("a.txt:dest.txt", out var r1, out _));
        Assert.True(RemapRule.TryParse("b.txt:dest.txt", out var r2, out _));
        var mappings = RemapFilesystem.DryRunRemap(src, new List<RemapRule> { r1!, r2! });
        // Both host files map to same guest dest.txt, but first wins, second omitted via guestSeen dedup
        Assert.Single(mappings);
        Assert.Equal("/a.txt", mappings[0].HostPath);
        Assert.Equal("/dest.txt", mappings[0].ImagePath);
    }

    // -----------------------------------------------------------------
    // BuildImage
    // -----------------------------------------------------------------

    [Fact]
    public void BuildImage_SimpleRemap_CreatesIso()
    {
        var src = CreateTempDir();
        CreateFile(src, "orig/a.txt", "hello");
        CreateFile(src, "orig/sub/b.txt", "world");
        CreateFile(src, "ignore.txt", "ignore");

        Assert.True(RemapRule.TryParse("orig/**:new/{1}", out var rule, out _));
        var outDir = CreateTempDir();
        var isoPath = Path.Combine(outDir, "remap.iso");

        int res = RemapFilesystem.BuildImage(src, isoPath, new List<RemapRule> { rule! });
        Assert.Equal(0, res);
        Assert.True(File.Exists(isoPath));

        // Extract and verify structure
        var extract = CreateTempDir();
        int ext = XisoReader.Extract(isoPath, extract, false);
        Assert.Equal(0, ext);
        Assert.True(File.Exists(Path.Combine(extract, "new", "a.txt")));
        Assert.True(File.Exists(Path.Combine(extract, "new", "sub", "b.txt")));
        Assert.False(File.Exists(Path.Combine(extract, "ignore.txt")));
        Assert.False(File.Exists(Path.Combine(extract, "orig", "a.txt")));
        Assert.Equal("hello", File.ReadAllText(Path.Combine(extract, "new", "a.txt")));
    }

    [Fact]
    public void BuildImage_Exclusion_CreatesIsoWithoutExcluded()
    {
        var src = CreateTempDir();
        CreateFile(src, "keep.txt", "keep");
        CreateFile(src, "skip.tmp", "skip");
        CreateFile(src, "sub/keep2.txt", "keep2");
        CreateFile(src, "sub/skip2.tmp", "skip2");

        Assert.True(RemapRule.TryParse("**:{0}", out var all, out _));
        Assert.True(RemapRule.TryParse("!**/*.tmp", out var excl, out _));
        var outDir = CreateTempDir();
        var isoPath = Path.Combine(outDir, "remap_excl.iso");

        int res = RemapFilesystem.BuildImage(src, isoPath, new List<RemapRule> { all!, excl! });
        Assert.Equal(0, res);
        var extract = CreateTempDir();
        XisoReader.Extract(isoPath, extract, false);
        Assert.True(File.Exists(Path.Combine(extract, "keep.txt")));
        Assert.True(File.Exists(Path.Combine(extract, "sub", "keep2.txt")));
        Assert.False(File.Exists(Path.Combine(extract, "skip.tmp")));
        Assert.False(File.Exists(Path.Combine(extract, "sub", "skip2.tmp")));
    }

    [Fact]
    public void BuildImage_NoRules_ReturnsError()
    {
        var src = CreateTempDir();
        CreateFile(src, "a.txt", "a");
        var outDir = CreateTempDir();
        var isoPath = Path.Combine(outDir, "no_rules.iso");
        int res = RemapFilesystem.BuildImage(src, isoPath, new List<RemapRule>());
        Assert.Equal(1, res);
    }

    [Fact]
    public void BuildImage_MissingSource_ReturnsError()
    {
        var missing = Path.Combine(Path.GetTempPath(), $"missing_{Guid.NewGuid():N}");
        var outDir = CreateTempDir();
        var isoPath = Path.Combine(outDir, "missing.iso");
        Assert.True(RemapRule.TryParse("**:{0}", out var r, out _));
        int res = RemapFilesystem.BuildImage(missing, isoPath, new List<RemapRule> { r! });
        Assert.Equal(1, res);
    }
}