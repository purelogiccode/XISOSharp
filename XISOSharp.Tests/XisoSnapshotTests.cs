using System.Security.Cryptography;

namespace XISOSharp.Tests;

/// <summary>
/// Snapshot tests for TODO #1 (full test suite, xdvdfs #107/#137): a fixed,
/// deterministic source tree is packed with <c>fileTime: 0</c>, and the
/// resulting image bytes must equal the checked-in
/// <c>XISOSharp.Tests/Fixtures/test_fixture.iso</c> reference exactly.
/// Extraction (and rewrite + extraction) must reproduce every source file
/// byte-for-byte (SHA-256 per file), including empty files/directories,
/// nested structure, non-ASCII names, and the <c>.xbe</c> media patch.
/// </summary>
[Collection("Sequential")]
public class XisoSnapshotTests : IDisposable
{
    private static readonly string FixtureIsoPath = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "Fixtures", "test_fixture.iso"));

    private const ulong DeterministicFileTime = 0UL;
    private const string FixtureVolumeName = "test_fixture";

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

    /// <summary>
    /// Builds the canonical snapshot source tree. All content is fully
    /// deterministic (inline xorshift, no RNG version dependence, no clock).
    /// </summary>
    internal static void BuildSnapshotTree(string root)
    {
        File.WriteAllText(Path.Combine(root, "readme.txt"),
            string.Concat(Enumerable.Repeat("XISOSharp snapshot fixture v1\n", 10)));
        File.WriteAllBytes(Path.Combine(root, "empty.txt"), []);

        var binary = new byte[1024];
        for (var i = 0; i < binary.Length; i++) binary[i] = (byte)(i % 256);
        File.WriteAllBytes(Path.Combine(root, "binary.bin"), binary);

        File.WriteAllBytes(Path.Combine(root, "large.bin"), XorShiftBytes(0x51A9u, 1024 * 1024));

        var nested = Path.Combine(root, "nested", "level1", "level2");
        Directory.CreateDirectory(nested);
        File.WriteAllText(Path.Combine(nested, "deep.txt"), "deeply nested content\n");
        File.WriteAllText(Path.Combine(root, "nested", "sibling.txt"), "sibling content\n");

        Directory.CreateDirectory(Path.Combine(root, "emptydir"));

        File.WriteAllText(Path.Combine(root, "caf\u00e9.txt"), "non-ascii name content\n");

        var sysUpdate = Path.Combine(root, "$SystemUpdate");
        Directory.CreateDirectory(sysUpdate);
        File.WriteAllText(Path.Combine(sysUpdate, "manifest.txt"), "system update manifest\n");

        // Small .xbe carrying the media-enable pattern twice: the writer patches
        // byte 7 of every occurrence (0x7D -> 0xEB), so the snapshot also locks
        // the patcher behavior into the reference bytes.
        var xbe = new byte[4096];
        Array.Fill(xbe, (byte)0xCC);
        Constants.MediaEnable.CopyTo(xbe.AsSpan(100));
        Constants.MediaEnable.CopyTo(xbe.AsSpan(3000));
        File.WriteAllBytes(Path.Combine(root, "patchme.xbe"), xbe);
    }

    private static byte[] XorShiftBytes(uint seed, int count)
    {
        var outBuf = new byte[count];
        var state = seed == 0 ? 1u : seed;
        for (var i = 0; i < count; i++)
        {
            state ^= state << 13;
            state ^= state >> 17;
            state ^= state << 5;
            outBuf[i] = (byte)(state & 0xFF);
        }

        return outBuf;
    }

    /// <summary>
    /// The .xbe bytes as they must appear after create → extract: every
    /// media-enable pattern occurrence patched at byte 7.
    /// </summary>
    internal static byte[] ExpectedPatchedXbe(byte[] source)
    {
        var patched = (byte[])source.Clone();
        var pattern = Constants.MediaEnable;
        for (var i = 0; i + pattern.Length <= patched.Length; i++)
        {
            if (!patched.AsSpan(i, pattern.Length).SequenceEqual(pattern)) continue;
            patched[i + Constants.MediaEnableBytePos] = Constants.MediaEnableByte;
            i += Constants.MediaEnableLength - 1;
        }

        return patched;
    }

    private static string CreateSnapshotIso(string srcDir, string isoDir)
    {
        var rc = XisoWriter.CreateXiso(srcDir, isoDir, null, null, out var isoPath,
            FixtureVolumeName, null, fileTime: DeterministicFileTime);
        Assert.Equal(0, rc);
        Assert.NotNull(isoPath);
        return isoPath;
    }

    private static Dictionary<string, byte[]> HashTree(string root)
    {
        var hashes = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var file in Directory.GetFiles(root, "*", SearchOption.AllDirectories))
            hashes[Path.GetRelativePath(root, file)] = SHA256.HashData(File.ReadAllBytes(file));
        return hashes;
    }

    [Fact]
    public void Snapshot_CreateTwiceWithFileTimeZero_ByteIdentical()
    {
        var src = CreateTempDir("xiso_snap_src");
        BuildSnapshotTree(src);

        var first = CreateSnapshotIso(src, CreateTempDir("xiso_snap_a"));
        var second = CreateSnapshotIso(src, CreateTempDir("xiso_snap_b"));

        Assert.Equal(SHA256.HashData(File.ReadAllBytes(first)), SHA256.HashData(File.ReadAllBytes(second)));
    }

    [Fact]
    public void Snapshot_MatchesCheckedInFixture()
    {
        Assert.True(File.Exists(FixtureIsoPath),
            $"Reference fixture missing: {FixtureIsoPath}. Regenerate with XISO_UPDATE_FIXTURE=1 " +
            "dotnet test --filter FullyQualifiedName~RegenerateFixtureIso_WhenRequested");

        var src = CreateTempDir("xiso_snap_src");
        BuildSnapshotTree(src);
        var created = CreateSnapshotIso(src, CreateTempDir("xiso_snap_out"));

        var expected = File.ReadAllBytes(FixtureIsoPath);
        var actual = File.ReadAllBytes(created);
        Assert.True(expected.Length > 1024 * 1024, $"Fixture unexpectedly small: {expected.Length} bytes");
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void Snapshot_FixtureExtract_AllFileHashesMatchSource()
    {
        Assert.True(File.Exists(FixtureIsoPath), $"Reference fixture missing: {FixtureIsoPath}");

        var src = CreateTempDir("xiso_snap_src");
        BuildSnapshotTree(src);

        var extractDir = CreateTempDir("xiso_snap_ext");
        Assert.Equal(0, XisoReader.Extract(FixtureIsoPath, extractDir, false));

        // The .xbe on disk is pre-patch; everything extracted must be post-patch.
        var expected = HashTree(src);
        expected["patchme.xbe"] = SHA256.HashData(
            ExpectedPatchedXbe(File.ReadAllBytes(Path.Combine(src, "patchme.xbe"))));

        var actual = HashTree(extractDir);
        Assert.Equal(expected.Keys.OrderBy(static k => k, StringComparer.Ordinal),
            actual.Keys.OrderBy(static k => k, StringComparer.Ordinal));
        foreach (var (rel, hash) in expected)
            Assert.True(actual[rel].SequenceEqual(hash), $"content mismatch after extract: {rel}");

        // Empty directories have no files to hash: check structure explicitly.
        Assert.True(Directory.Exists(Path.Combine(extractDir, "emptydir")), "emptydir missing after extract");
        Assert.True(Directory.Exists(Path.Combine(extractDir, "nested", "level1", "level2")),
            "nested structure missing after extract");
    }

    [Fact]
    public void Snapshot_RewriteThenExtract_PreservesAllContent()
    {
        Assert.True(File.Exists(FixtureIsoPath), $"Reference fixture missing: {FixtureIsoPath}");

        var rewriteDir = CreateTempDir("xiso_snap_rw");
        Assert.Equal(0, XisoReader.Rewrite(FixtureIsoPath, rewriteDir, out var rewritten));
        Assert.NotNull(rewritten);

        var src = CreateTempDir("xiso_snap_src");
        BuildSnapshotTree(src);
        var expected = HashTree(src);
        expected["patchme.xbe"] = SHA256.HashData(
            ExpectedPatchedXbe(File.ReadAllBytes(Path.Combine(src, "patchme.xbe"))));

        var extractDir = CreateTempDir("xiso_snap_rwext");
        Assert.Equal(0, XisoReader.Extract(rewritten, extractDir, false));

        var actual = HashTree(extractDir);
        Assert.Equal(expected.Keys.OrderBy(static k => k, StringComparer.Ordinal),
            actual.Keys.OrderBy(static k => k, StringComparer.Ordinal));
        foreach (var (rel, hash) in expected)
            Assert.True(actual[rel].SequenceEqual(hash), $"content mismatch after rewrite+extract: {rel}");
    }

    /// <summary>
    /// Validates that a fresh <see cref="BuildSnapshotTree"/> pack matches the
    /// checked-in <c>Fixtures/test_fixture.iso</c> reference. Runs only when
    /// <c>XISO_UPDATE_FIXTURE=1</c> is set (otherwise skipped at discovery);
    /// it validates against a temp copy and never overwrites the fixture binary.
    /// After a legitimate writer change, regenerate the binary out of band,
    /// inspect the diff and commit it.
    /// </summary>
    [RequiresUpdateFixtureFact]
    public void RegenerateFixtureIso_WhenRequested()
    {
        var src = CreateTempDir("xiso_snap_src");
        BuildSnapshotTree(src);
        var created = CreateSnapshotIso(src, CreateTempDir("xiso_snap_out"));

        var tempCopy = Path.Combine(CreateTempDir("xiso_snap_regen"), "test_fixture.iso");
        File.Copy(created, tempCopy);

        var expected = File.ReadAllBytes(FixtureIsoPath);
        var actual = File.ReadAllBytes(tempCopy);
        Assert.Equal(expected, actual);
    }
}