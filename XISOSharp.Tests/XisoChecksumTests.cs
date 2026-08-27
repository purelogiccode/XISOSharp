using System.Security.Cryptography;
using System.Text;

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
        foreach (var dir in _tempDirs)
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
                try { File.Delete(dir); }
                catch { }
            }
        }
    }

    private string CreateTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"xiso_chk_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        _tempDirs.Add(dir);
        return dir;
    }

    private string CreateSourceDir(Action<string> populate)
    {
        var src = Path.Combine(Path.GetTempPath(), $"xiso_chk_src_{Guid.NewGuid():N}");
        Directory.CreateDirectory(src);
        _tempDirs.Add(src);
        populate(src);
        return src;
    }

    private string CreateIso(string srcDir, string? outputDir = null, int? prependSectors = null)
    {
        outputDir ??= CreateTempDir();
        var result = XisoWriter.CreateXiso(srcDir, outputDir, null, null, out var isoPath, null, null,
            prependSectors: prependSectors);
        Assert.Equal(0, result);
        Assert.NotNull(isoPath);
        Assert.True(File.Exists(isoPath));
        // isoPath is inside outputDir which is already tracked; also track file explicitly for cleanup if needed
        return isoPath!;
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
        var src1 = CreateSourceDir(PopulateSimple);
        var src2 = CreateSourceDir(PopulateSimple);

        var iso1 = CreateIso(src1);
        var iso2 = CreateIso(src2);

        var hash1 = XisoChecksum.ComputeImageChecksum(iso1);
        var hash2 = XisoChecksum.ComputeImageChecksum(iso2);

        Assert.Equal(hash1, hash2);
    }

    [Fact]
    public void ComputeImageChecksum_DifferentContent_DifferentChecksum()
    {
        var src1 = CreateSourceDir(d => File.WriteAllText(Path.Combine(d, "a.txt"), "content A"));
        var src2 = CreateSourceDir(d => File.WriteAllText(Path.Combine(d, "a.txt"), "content B"));

        var iso1 = CreateIso(src1);
        var iso2 = CreateIso(src2);

        var hash1 = XisoChecksum.ComputeImageChecksum(iso1);
        var hash2 = XisoChecksum.ComputeImageChecksum(iso2);

        Assert.NotEqual(hash1, hash2);
    }

    [Fact]
    public void ComputeImageChecksum_EmptyDirectory_ProducesValidHash()
    {
        var src = CreateSourceDir(_ => { });
        var iso = CreateIso(src);

        var hash = XisoChecksum.ComputeImageChecksum(iso);
        var hex = XisoChecksum.ComputeImageChecksumHex(iso);

        Assert.NotNull(hash);
        Assert.Equal(32, hash.Length);
        Assert.Equal(64, hex.Length);
        // SHA3-256 hex is lowercase
        Assert.Equal(hex, hex.ToLowerInvariant());
    }

    [Fact]
    public void ComputeImageChecksum_HexLength64AndLowercase()
    {
        var src = CreateSourceDir(d => File.WriteAllText(Path.Combine(d, "file.txt"), "data"));
        var iso = CreateIso(src);

        var hex = XisoChecksum.ComputeImageChecksumHex(iso);

        Assert.Equal(64, hex.Length);
        // Ensure hex string contains only 0-9 a-f
        Assert.Matches("^[0-9a-f]{64}$", hex);
    }

    [Fact]
    public void ComputeImageChecksum_BytesAndHexAreConsistent()
    {
        var src = CreateSourceDir(d => File.WriteAllText(Path.Combine(d, "file.txt"), "consistency check"));
        var iso = CreateIso(src);

        var bytes = XisoChecksum.ComputeImageChecksum(iso);
        var hex = XisoChecksum.ComputeImageChecksumHex(iso);
        var hexFromBytes = Convert.ToHexString(bytes).ToLowerInvariant();

        Assert.Equal(hexFromBytes, hex);
    }

    [Fact]
    public void ComputeImageChecksum_FileStreamOverloadMatchesPathOverload()
    {
        var src = CreateSourceDir(d => File.WriteAllText(Path.Combine(d, "file.txt"), "stream vs path"));
        var iso = CreateIso(src);

        var hashViaPath = XisoChecksum.ComputeImageChecksum(iso);

        using var fs = new FileStream(iso, FileMode.Open, FileAccess.Read, FileShare.Read, 65536);
        var hashViaStream = XisoChecksum.ComputeImageChecksum(fs, Path.GetFileName(iso));

        Assert.Equal(hashViaPath, hashViaStream);
    }

    [Fact]
    public void ComputeImageChecksum_CaseSensitivity_ProducesDifferentChecksum()
    {
        var srcLower = CreateSourceDir(d => File.WriteAllText(Path.Combine(d, "hello.txt"), "same content"));
        var srcUpper = CreateSourceDir(d => File.WriteAllText(Path.Combine(d, "HELLO.txt"), "same content"));

        var isoLower = CreateIso(srcLower);
        var isoUpper = CreateIso(srcUpper);

        var hashLower = XisoChecksum.ComputeImageChecksum(isoLower);
        var hashUpper = XisoChecksum.ComputeImageChecksum(isoUpper);

        Assert.NotEqual(hashLower, hashUpper);
    }

    [Fact]
    public void ComputeImageChecksum_FilesOrdering_Deterministic()
    {
        // Same logical files but created in opposite order; checksum must still match because
        // XisoChecksum sorts paths ordinally.
        var src1 = CreateSourceDir(d =>
        {
            File.WriteAllText(Path.Combine(d, "a.txt"), "alpha");
            File.WriteAllText(Path.Combine(d, "b.txt"), "beta");
            File.WriteAllText(Path.Combine(d, "c.txt"), "gamma");
        });
        var src2 = CreateSourceDir(d =>
        {
            File.WriteAllText(Path.Combine(d, "c.txt"), "gamma");
            File.WriteAllText(Path.Combine(d, "b.txt"), "beta");
            File.WriteAllText(Path.Combine(d, "a.txt"), "alpha");
        });

        var iso1 = CreateIso(src1);
        var iso2 = CreateIso(src2);

        var hash1 = XisoChecksum.ComputeImageChecksum(iso1);
        var hash2 = XisoChecksum.ComputeImageChecksum(iso2);

        Assert.Equal(hash1, hash2);
        // Also hex variant deterministic
        Assert.Equal(XisoChecksum.ComputeImageChecksumHex(iso1), XisoChecksum.ComputeImageChecksumHex(iso2));
    }

    [Fact]
    public void ComputeImageChecksum_SkipSectorsOverload_MatchesNonPrependedChecksum()
    {
        // Create normal and prepended ISOs from the same source; checksums should match
        // when the prepended one is read with the correct skip offset.
        var src = CreateSourceDir(PopulateSimple);

        var normalIso = CreateIso(src, prependSectors: null);
        var prependedIso = CreateIso(src, prependSectors: 64);

        var hashNormal = XisoChecksum.ComputeImageChecksum(normalIso);
        var hashPrependedViaSkip = XisoChecksum.ComputeImageChecksum(prependedIso, skipSectors: 64);

        Assert.Equal(hashNormal, hashPrependedViaSkip);

        // Hex overload with skip should also match
        var hexNormal = XisoChecksum.ComputeImageChecksumHex(normalIso);
        var hexPrepended = XisoChecksum.ComputeImageChecksumHex(prependedIso, skipSectors: 64);
        Assert.Equal(hexNormal, hexPrepended);
    }

    [Fact]
    public void ComputeImageChecksum_SkipSectors_FileStreamOverloadMatchesPathOverload()
    {
        var src = CreateSourceDir(d => File.WriteAllText(Path.Combine(d, "file.txt"), "skip stream"));
        var prependedIso = CreateIso(src, prependSectors: 32);

        var hashViaPath = XisoChecksum.ComputeImageChecksum(prependedIso, skipSectors: 32);

        using var fs = new FileStream(prependedIso, FileMode.Open, FileAccess.Read, FileShare.Read, 65536);
        var hashViaStream = XisoChecksum.ComputeImageChecksum(fs, Path.GetFileName(prependedIso), skipSectors: 32);

        Assert.Equal(hashViaPath, hashViaStream);
    }

    [Fact]
    public void ComputeImageChecksum_NestedDirectoryVsFlat_DifferentChecksum()
    {
        var srcFlat = CreateSourceDir(d =>
        {
            File.WriteAllText(Path.Combine(d, "a.txt"), "content");
            File.WriteAllText(Path.Combine(d, "b.txt"), "content");
        });
        var srcNested = CreateSourceDir(d =>
        {
            File.WriteAllText(Path.Combine(d, "a.txt"), "content");
            Directory.CreateDirectory(Path.Combine(d, "sub"));
            File.WriteAllText(Path.Combine(d, "sub", "b.txt"), "content");
        });

        var isoFlat = CreateIso(srcFlat);
        var isoNested = CreateIso(srcNested);

        var hashFlat = XisoChecksum.ComputeImageChecksum(isoFlat);
        var hashNested = XisoChecksum.ComputeImageChecksum(isoNested);

        Assert.NotEqual(hashFlat, hashNested);
    }

    [Fact]
    public void ComputeImageChecksum_CancellationToken_ThrowsWhenCancelled()
    {
        var src = CreateSourceDir(d => File.WriteAllText(Path.Combine(d, "file.txt"), "cancel"));
        var iso = CreateIso(src);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.Throws<OperationCanceledException>(() => XisoChecksum.ComputeImageChecksum(iso, ct: cts.Token));

        using var fs = new FileStream(iso, FileMode.Open, FileAccess.Read, FileShare.Read, 65536);
        Assert.Throws<OperationCanceledException>(() => XisoChecksum.ComputeImageChecksum(fs, "iso", ct: cts.Token));
    }

    [Fact]
    public void ComputeImageChecksumHex_IsLowercaseAndMatchesBytes()
    {
        var src = CreateSourceDir(d =>
        {
            File.WriteAllText(Path.Combine(d, "alpha.txt"), "alpha");
            Directory.CreateDirectory(Path.Combine(d, "beta"));
            File.WriteAllText(Path.Combine(d, "beta", "gamma.bin"), new string('x', 1000));
        });
        var iso = CreateIso(src);

        var hex = XisoChecksum.ComputeImageChecksumHex(iso);
        var bytes = XisoChecksum.ComputeImageChecksum(iso);

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