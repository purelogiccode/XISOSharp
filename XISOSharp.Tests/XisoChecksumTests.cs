using XISOSharp.BlockDevice;

namespace XISOSharp.Tests;

/// <summary>
/// Tests for <see cref="XisoChecksum"/> — deterministic SHA3-256 checksums over
/// XISO image contents (path bytes + file data, sorted ordinal).
/// </summary>
[Collection("Sequential")]
public class XisoChecksumTests : IDisposable
{
    private readonly List<string> _tempDirs = [];

    public void Dispose()
    {
        foreach (string dir in _tempDirs)
        {
            try
            {
                if (Directory.Exists(dir)) Directory.Delete(dir, true);
            }
            catch
            {
                /* best effort */
            }

            if (File.Exists(dir))
            {
                try
                {
                    File.Delete(dir);
                }
                catch
                {
                    // ignored
                }
            }
        }
    }

    private string CreateTempDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"xiso_chk_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        _tempDirs.Add(dir);
        return dir;
    }

    private string CreateSourceDir(Action<string> populate)
    {
        string src = Path.Combine(Path.GetTempPath(), $"xiso_chk_src_{Guid.NewGuid():N}");
        Directory.CreateDirectory(src);
        _tempDirs.Add(src);
        populate(src);
        return src;
    }

    private string CreateIso(string srcDir, string? outputDir = null, int? prependSectors = null)
    {
        outputDir ??= CreateTempDir();
        int result = XisoWriter.CreateXiso(srcDir, outputDir, null, null, out string? isoPath, null, null,
            prependSectors: prependSectors);
        Assert.Equal(0, result);
        Assert.NotNull(isoPath);
        Assert.True(File.Exists(isoPath));
        // isoPath is inside outputDir which is already tracked; also track file explicitly for cleanup if needed
        return isoPath;
    }

    private static void PopulateSimple(string dir)
    {
        File.WriteAllText(Path.Combine(dir, "file1.txt"), "hello world");
        File.WriteAllText(Path.Combine(dir, "file2.txt"), "second file");
        Directory.CreateDirectory(Path.Combine(dir, "subdir"));
        File.WriteAllText(Path.Combine(dir, "subdir", "nested.txt"), "nested content");
    }

    [Fact]
    public void ComputeImageChecksum_Deterministic_SameContentSameChecksum()
    {
        string src1 = CreateSourceDir(PopulateSimple);
        string src2 = CreateSourceDir(PopulateSimple);

        string iso1 = CreateIso(src1);
        string iso2 = CreateIso(src2);

        byte[] hash1 = XisoChecksum.ComputeImageChecksum(iso1);
        byte[] hash2 = XisoChecksum.ComputeImageChecksum(iso2);

        Assert.Equal(hash1, hash2);
    }

    [Fact]
    public void ComputeImageChecksum_DifferentContent_DifferentChecksum()
    {
        string src1 = CreateSourceDir(d => File.WriteAllText(Path.Combine(d, "a.txt"), "content A"));
        string src2 = CreateSourceDir(d => File.WriteAllText(Path.Combine(d, "a.txt"), "content B"));

        string iso1 = CreateIso(src1);
        string iso2 = CreateIso(src2);

        byte[] hash1 = XisoChecksum.ComputeImageChecksum(iso1);
        byte[] hash2 = XisoChecksum.ComputeImageChecksum(iso2);

        Assert.NotEqual(hash1, hash2);
    }

    [Fact]
    public void ComputeImageChecksum_EmptyDirectory_ProducesValidHash()
    {
        string src = CreateSourceDir(_ => { });
        string iso = CreateIso(src);

        byte[] hash = XisoChecksum.ComputeImageChecksum(iso);
        string hex = XisoChecksum.ComputeImageChecksumHex(iso);

        Assert.NotNull(hash);
        Assert.Equal(32, hash.Length);
        Assert.Equal(64, hex.Length);
        // BUG-TEST-018: tautology-adjacent self-compare replaced with a real
        // lowercase-hex shape check (stronger regex already covers this elsewhere).
        Assert.Matches("^[0-9a-f]{64}$", hex);
    }

    [Fact]
    public void ComputeImageChecksum_HexLength64AndLowercase()
    {
        string src = CreateSourceDir(d => File.WriteAllText(Path.Combine(d, "file.txt"), "data"));
        string iso = CreateIso(src);

        string hex = XisoChecksum.ComputeImageChecksumHex(iso);

        Assert.Equal(64, hex.Length);
        // Ensure hex string contains only 0-9 a-f
        Assert.Matches("^[0-9a-f]{64}$", hex);
    }

    [Fact]
    public void ComputeImageChecksum_BytesAndHexAreConsistent()
    {
        string src = CreateSourceDir(d => File.WriteAllText(Path.Combine(d, "file.txt"), "consistency check"));
        string iso = CreateIso(src);

        byte[] bytes = XisoChecksum.ComputeImageChecksum(iso);
        string hex = XisoChecksum.ComputeImageChecksumHex(iso);
        string hexFromBytes = Convert.ToHexString(bytes).ToLowerInvariant();

        Assert.Equal(hexFromBytes, hex);
    }

    [Fact]
    public void ComputeImageChecksum_FileStreamOverloadMatchesPathOverload()
    {
        string src = CreateSourceDir(d => File.WriteAllText(Path.Combine(d, "file.txt"), "stream vs path"));
        string iso = CreateIso(src);

        byte[] hashViaPath = XisoChecksum.ComputeImageChecksum(iso);

        using FileStream fs = new(iso, FileMode.Open, FileAccess.Read, FileShare.Read, 65536);
        byte[] hashViaStream = XisoChecksum.ComputeImageChecksum(fs, Path.GetFileName(iso));

        Assert.Equal(hashViaPath, hashViaStream);
    }

    [Fact]
    public void ComputeImageChecksum_CaseSensitivity_ProducesDifferentChecksum()
    {
        string srcLower = CreateSourceDir(d => File.WriteAllText(Path.Combine(d, "hello.txt"), "same content"));
        string srcUpper = CreateSourceDir(d => File.WriteAllText(Path.Combine(d, "HELLO.txt"), "same content"));

        string isoLower = CreateIso(srcLower);
        string isoUpper = CreateIso(srcUpper);

        byte[] hashLower = XisoChecksum.ComputeImageChecksum(isoLower);
        byte[] hashUpper = XisoChecksum.ComputeImageChecksum(isoUpper);

        Assert.NotEqual(hashLower, hashUpper);
    }

    [Fact]
    public void ComputeImageChecksum_FilesOrdering_Deterministic()
    {
        // Same logical files but created in opposite order; checksum must still match because
        // XisoChecksum sorts paths ordinally.
        string src1 = CreateSourceDir(d =>
        {
            File.WriteAllText(Path.Combine(d, "a.txt"), "alpha");
            File.WriteAllText(Path.Combine(d, "b.txt"), "beta");
            File.WriteAllText(Path.Combine(d, "c.txt"), "gamma");
        });
        string src2 = CreateSourceDir(d =>
        {
            File.WriteAllText(Path.Combine(d, "c.txt"), "gamma");
            File.WriteAllText(Path.Combine(d, "b.txt"), "beta");
            File.WriteAllText(Path.Combine(d, "a.txt"), "alpha");
        });

        string iso1 = CreateIso(src1);
        string iso2 = CreateIso(src2);

        byte[] hash1 = XisoChecksum.ComputeImageChecksum(iso1);
        byte[] hash2 = XisoChecksum.ComputeImageChecksum(iso2);

        Assert.Equal(hash1, hash2);
        // Also hex variant deterministic
        Assert.Equal(XisoChecksum.ComputeImageChecksumHex(iso1), XisoChecksum.ComputeImageChecksumHex(iso2));
    }

    [Fact]
    public void ComputeImageChecksum_SkipSectorsOverload_MatchesNonPrependedChecksum()
    {
        // Create normal and prepended ISOs from the same source; checksums should match
        // when the prepended one is read with the correct skip offset.
        string src = CreateSourceDir(PopulateSimple);

        string normalIso = CreateIso(src, prependSectors: null);
        string prependedIso = CreateIso(src, prependSectors: 64);

        byte[] hashNormal = XisoChecksum.ComputeImageChecksum(normalIso);
        byte[] hashPrependedViaSkip = XisoChecksum.ComputeImageChecksum(prependedIso, skipSectors: 64);

        Assert.Equal(hashNormal, hashPrependedViaSkip);

        // Hex overload with skip should also match
        string hexNormal = XisoChecksum.ComputeImageChecksumHex(normalIso);
        string hexPrepended = XisoChecksum.ComputeImageChecksumHex(prependedIso, skipSectors: 64);
        Assert.Equal(hexNormal, hexPrepended);
    }

    [Fact]
    public void ComputeImageChecksum_SkipSectors_FileStreamOverloadMatchesPathOverload()
    {
        string src = CreateSourceDir(d => File.WriteAllText(Path.Combine(d, "file.txt"), "skip stream"));
        string prependedIso = CreateIso(src, prependSectors: 32);

        byte[] hashViaPath = XisoChecksum.ComputeImageChecksum(prependedIso, skipSectors: 32);

        using FileStream fs = new(prependedIso, FileMode.Open, FileAccess.Read, FileShare.Read, 65536);
        byte[] hashViaStream = XisoChecksum.ComputeImageChecksum(fs, Path.GetFileName(prependedIso), skipSectors: 32);

        Assert.Equal(hashViaPath, hashViaStream);
    }

    [Fact]
    public void ComputeImageChecksum_CisoV2SingleFile_MatchesIsoChecksum()
    {
        string src = CreateSourceDir(d =>
        {
            File.WriteAllText(Path.Combine(d, "file.txt"), "ciso checksum single");
            Directory.CreateDirectory(Path.Combine(d, "sub"));
            File.WriteAllText(Path.Combine(d, "sub", "nested.bin"), new string('n', 5000));
        });
        string iso = CreateIso(src);

        Assert.Equal(0, CisoWriter.CompressToCso(iso, Path.ChangeExtension(iso, ".cso"), level: 9,
            splitBytes: null, version: CisoWriter.VersionLz4));
        string cso = Path.ChangeExtension(iso, ".cso");
        Assert.True(File.Exists(cso));

        // The .cso path is auto-detected and routed through CisoBlockDevice
        Assert.Equal(XisoChecksum.ComputeImageChecksumHex(iso), XisoChecksum.ComputeImageChecksumHex(cso));
    }

    [Fact]
    public void ComputeImageChecksum_CisoV1SingleFile_MatchesIsoChecksum()
    {
        string src = CreateSourceDir(d =>
        {
            File.WriteAllText(Path.Combine(d, "file.txt"), "ciso v1 checksum");
            Directory.CreateDirectory(Path.Combine(d, "sub"));
            File.WriteAllText(Path.Combine(d, "sub", "nested.bin"), new string('v', 5000));
        });
        string iso = CreateIso(src);

        Assert.Equal(0, CisoWriter.CompressToCso(iso, Path.ChangeExtension(iso, ".cso"), level: 6,
            splitBytes: null, version: CisoWriter.VersionDeflate));
        string cso = Path.ChangeExtension(iso, ".cso");

        Assert.Equal(XisoChecksum.ComputeImageChecksumHex(iso), XisoChecksum.ComputeImageChecksumHex(cso));
    }

    [Fact]
    public void ComputeImageChecksum_CisoV2SplitParts_MatchesIsoChecksum()
    {
        string src = CreateSourceDir(d =>
        {
            File.WriteAllText(Path.Combine(d, "file.txt"), "ciso checksum split");
            Directory.CreateDirectory(Path.Combine(d, "sub"));
            File.WriteAllText(Path.Combine(d, "sub", "nested.bin"), new string('s', 20000));
        });
        string iso = CreateIso(src);

        // Tiny split point forces multiple .N.cso parts
        Assert.Equal(0, CisoWriter.CompressToCso(iso, Path.ChangeExtension(iso, ".cso"), level: 9,
            splitBytes: 4096, version: CisoWriter.VersionLz4));
        string firstPart = Path.ChangeExtension(iso, ".1.cso");
        Assert.True(File.Exists(firstPart));
        Assert.True(File.Exists(Path.ChangeExtension(iso, ".2.cso")), "expected at least two split parts");

        // Checksum over the .1.cso split path must equal the uncompressed ISO checksum
        Assert.Equal(XisoChecksum.ComputeImageChecksumHex(iso), XisoChecksum.ComputeImageChecksumHex(firstPart));
    }

    [Fact]
    public void ComputeImageChecksum_CisoV1SplitParts_MatchesIsoChecksum()
    {
        string src = CreateSourceDir(d =>
        {
            File.WriteAllText(Path.Combine(d, "file.txt"), "ciso v1 checksum split");
            Directory.CreateDirectory(Path.Combine(d, "sub"));
            File.WriteAllText(Path.Combine(d, "sub", "nested.bin"), new string('w', 20000));
        });
        string iso = CreateIso(src);

        Assert.Equal(0, CisoWriter.CompressToCso(iso, Path.ChangeExtension(iso, ".cso"), level: 6,
            splitBytes: 4096, version: CisoWriter.VersionDeflate));
        string firstPart = Path.ChangeExtension(iso, ".1.cso");
        Assert.True(File.Exists(firstPart));

        Assert.Equal(XisoChecksum.ComputeImageChecksumHex(iso), XisoChecksum.ComputeImageChecksumHex(firstPart));
    }

    [Fact]
    public void ComputeImageChecksum_IBlockDeviceOverload_CisoDeviceMatchesIsoChecksum()
    {
        string src = CreateSourceDir(d => File.WriteAllText(Path.Combine(d, "file.txt"), "block device checksum"));
        string iso = CreateIso(src);

        Assert.Equal(0, CisoWriter.CompressToCso(iso, Path.ChangeExtension(iso, ".cso"), level: 9,
            splitBytes: null, version: CisoWriter.VersionLz4));

        using CisoBlockDevice dev = new(Path.ChangeExtension(iso, ".cso"));
        string viaDevice = Convert.ToHexString(XisoChecksum.ComputeImageChecksum(dev, "game.cso")).ToLowerInvariant();

        Assert.Equal(XisoChecksum.ComputeImageChecksumHex(iso), viaDevice);
    }

    [Fact]
    public void ComputeImageChecksum_NestedDirectoryVsFlat_DifferentChecksum()
    {
        string srcFlat = CreateSourceDir(d =>
        {
            File.WriteAllText(Path.Combine(d, "a.txt"), "content");
            File.WriteAllText(Path.Combine(d, "b.txt"), "content");
        });
        string srcNested = CreateSourceDir(d =>
        {
            File.WriteAllText(Path.Combine(d, "a.txt"), "content");
            Directory.CreateDirectory(Path.Combine(d, "sub"));
            File.WriteAllText(Path.Combine(d, "sub", "b.txt"), "content");
        });

        string isoFlat = CreateIso(srcFlat);
        string isoNested = CreateIso(srcNested);

        byte[] hashFlat = XisoChecksum.ComputeImageChecksum(isoFlat);
        byte[] hashNested = XisoChecksum.ComputeImageChecksum(isoNested);

        Assert.NotEqual(hashFlat, hashNested);
    }

    [Fact]
    public void ComputeImageChecksum_CancellationToken_ThrowsWhenCancelled()
    {
        string src = CreateSourceDir(d => File.WriteAllText(Path.Combine(d, "file.txt"), "cancel"));
        string iso = CreateIso(src);

        using CancellationTokenSource cts = new();
        cts.Cancel();

        Assert.Throws<OperationCanceledException>(() => XisoChecksum.ComputeImageChecksum(iso, ct: cts.Token));

        using FileStream fs = new(iso, FileMode.Open, FileAccess.Read, FileShare.Read, 65536);
        Assert.Throws<OperationCanceledException>(() => XisoChecksum.ComputeImageChecksum(fs, "iso", ct: cts.Token));
    }

    [Fact]
    public void ComputeImageChecksumHex_IsLowercaseAndMatchesBytes()
    {
        string src = CreateSourceDir(d =>
        {
            File.WriteAllText(Path.Combine(d, "alpha.txt"), "alpha");
            Directory.CreateDirectory(Path.Combine(d, "beta"));
            File.WriteAllText(Path.Combine(d, "beta", "gamma.bin"), new string('x', 1000));
        });
        string iso = CreateIso(src);

        string hex = XisoChecksum.ComputeImageChecksumHex(iso);
        byte[] bytes = XisoChecksum.ComputeImageChecksum(iso);

        Assert.Equal(64, hex.Length);
        Assert.Equal(Convert.ToHexString(bytes).ToLowerInvariant(), hex);
        // Ensure not uppercase
        Assert.DoesNotContain("A", hex, StringComparison.Ordinal);
        Assert.DoesNotContain("B", hex, StringComparison.Ordinal);
        Assert.DoesNotContain("C", hex, StringComparison.Ordinal);
        Assert.DoesNotContain("D", hex, StringComparison.Ordinal);
        Assert.DoesNotContain("E", hex, StringComparison.Ordinal);
        Assert.DoesNotContain("F", hex, StringComparison.Ordinal);
    }
}
