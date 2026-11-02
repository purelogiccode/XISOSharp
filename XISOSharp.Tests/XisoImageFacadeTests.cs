using System.Security.Cryptography;
using XISOSharp.Models;
using XISOSharp.TestDataGenerator;

namespace XISOSharp.Tests;

/// <summary>
/// Tests for the open-once <see cref="XisoImage"/> facade, full-image hashes,
/// title metadata and <c>.hash</c> verification.
/// </summary>
[Collection("Sequential")]
public class XisoImageFacadeTests : IDisposable
{
    private readonly List<string> _tempDirs = [];

    public XisoImageFacadeTests()
    {
        Logger.Quiet = true;
        Logger.RealQuiet = true;
    }

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
                // best effort cleanup
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

    private static string TestIso => TestDataLocator.GetOutputIsoPath();

    [Fact]
    public void Open_ExposesVolumeAndListsEntries()
    {
        using XisoImage image = XisoImage.Open(TestIso);

        Assert.Equal(XisoImageFormat.Iso, image.Source.Format);
        Assert.True(image.Volume.IsValid);
        Assert.Equal("RAW", image.Volume.DiscFormat);
        Assert.True(image.Length > 0);

        IReadOnlyList<ExplorerNode> root = image.List();
        Assert.Contains(root, static entry => !entry.IsDirectory && string.Equals(entry.Name, "file1.txt", StringComparison.Ordinal));
        Assert.Contains(root, static entry => entry.IsDirectory && string.Equals(entry.Name, "subdir", StringComparison.Ordinal));

        IReadOnlyList<ExplorerNode> flat = image.ListFlat();
        Assert.Contains(flat, static entry => string.Equals(entry.FullPath, "/subdir/nested/deep.txt", StringComparison.Ordinal));
        Assert.True(flat.Count >= root.Count);
    }

    [Fact]
    public void ReadFile_AndRange_MatchImageBytes()
    {
        using XisoImage image = XisoImage.Open(TestIso);
        ExplorerNode node = image.GetNode("/file1.txt")!;
        byte[] expected = image.ReadFile("/file1.txt");
        Assert.Equal(node.Size, expected.Length);

        byte[] range = new byte[5];
        int read = image.ReadFile("/file1.txt", range, 2);
        Assert.Equal(5, read);
        Assert.Equal(expected.AsSpan(2, 5).ToArray(), range);

        using Stream stream = image.OpenReadStream("/subdir/nested/deep.txt");
        Assert.True(stream.CanSeek);
        Assert.True(stream.Length > 0);
    }

    [Fact]
    public void Extract_ProducesTheSameTree()
    {
        string output = CreateTempDir("xiso_facade_extract");
        using XisoImage image = XisoImage.Open(TestIso);

        int result = image.Extract(output);

        Assert.Equal(0, result);
        Assert.True(File.Exists(Path.Combine(output, "file1.txt")));
        Assert.True(File.Exists(Path.Combine(output, "subdir", "nested", "deep.txt")));
    }

    [Fact]
    public async Task OpenAsync_And_ExtractAsync_Work()
    {
        string output = CreateTempDir("xiso_facade_async");
        await using XisoImage image = await XisoImage.OpenAsync(TestIso);

        Assert.True(image.Volume.IsValid);
        int result = await image.ExtractAsync(output);

        Assert.Equal(0, result);
        Assert.True(File.Exists(Path.Combine(output, "file2.txt")));
    }

    [Fact]
    public void GetTitleInfo_ReadsDefaultXbeCertificate()
    {
        string source = CreateTempDir("xiso_title_src");
        File.WriteAllBytes(Path.Combine(source, "default.xbe"), XbeInfoTests.BuildXbe());
        string output = CreateTempDir("xiso_title_out");

        int result = XisoWriter.CreateXiso(source, output, null, null, out string? isoPath, null, null);
        Assert.Equal(0, result);
        Assert.NotNull(isoPath);

        using XisoImage image = XisoImage.Open(isoPath);
        TitleInfo? title = image.GetTitleInfo();

        Assert.NotNull(title);
        Assert.Equal("/default.xbe", title.XbePath);
        Assert.Equal("Halo: Combat Evolved", title.TitleName);
        Assert.Equal(0x4D530004u, title.TitleId);
        Assert.Equal("0x4D530004", title.TitleIdHex);
        Assert.Equal("Worldwide", title.GameRegionName);
        Assert.Equal(1u, title.DiskNumber);
        TitleInfo? staticTitle = XisoReader.GetTitleInfo(isoPath);
        Assert.NotNull(staticTitle);
        Assert.Equal(title.TitleName, staticTitle.TitleName);
        Assert.Equal(title.TitleId, staticTitle.TitleId);
        Assert.Equal(title.GameRegionName, staticTitle.GameRegionName);
    }

    [Fact]
    public void ComputeImageHash_MatchesRawSha256_AndIsDeterministic()
    {
        byte[] raw = File.ReadAllBytes(TestIso);
        XisoImageHashResult first = XisoImageHash.Compute(TestIso);
        XisoImageHashResult second = XisoImageHash.Compute(TestIso);

        Assert.Equal(raw.Length, first.TotalBytes);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(raw)).ToLowerInvariant(), first.Sha256);
        Assert.Equal(8, first.Crc32.Length);
        Assert.Equal(32, first.Md5.Length);
        Assert.Equal(40, first.Sha1.Length);
        Assert.Equal(64, first.Sha256.Length);
        Assert.Equal(64, first.Sha3256.Length);
        Assert.Equal(first, second);
    }

    [Fact]
    public void VerifyHashes_AcceptsPetrifySidecar_AndReportsMismatch()
    {
        string dir = CreateTempDir("xiso_verify_hash");
        string skeleton = Path.Combine(dir, "game.skeleton.xiso");
        string hashFile = Path.Combine(dir, "game.hash");

        Assert.True(XisoSkeleton.Petrify(TestIso, skeleton, hashFile, quiet: true));
        Assert.True(File.Exists(hashFile));

        HashVerificationResult pass = XisoSkeleton.VerifyHashes(TestIso, hashFile);
        Assert.True(pass.Passed);
        Assert.True(pass.Checked > 0);
        Assert.Equal(pass.Checked, pass.Matched);
        Assert.Equal(0, pass.Mismatched);
        Assert.Equal(0, pass.Missing);
        Assert.Equal(0, pass.Extra);

        // A skeleton has zeroed file data, so its per-file SHA-1s must differ.
        HashVerificationResult fail = XisoSkeleton.VerifyHashes(skeleton, hashFile);
        Assert.False(fail.Passed);
        Assert.True(fail.Mismatched > 0);
    }

    [Fact]
    public void Dispose_IsIdempotent_AndBlocksFurtherUse()
    {
        XisoImage image = XisoImage.Open(TestIso);
        image.Dispose();
        image.Dispose();

        Assert.Throws<ObjectDisposedException>(() => image.List());
    }
}
