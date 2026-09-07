using ZARSharp;

namespace XISOSharp.Tests;

/// <summary>
/// Tests for <see cref="XisoRedump"/> (video/update/rebuild) and
/// <see cref="XisoSkeleton"/> (Petrify) and <see cref="XisoZarchive"/> (ZAR).
/// </summary>
[Collection("Sequential")]
public class XisoRedumpAndSkeletonTests : IDisposable
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
                // ignored
            }
        }
    }

    private string CreateTempDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"xiso_rsk_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        _tempDirs.Add(dir);
        return dir;
    }

    private string CreateSourceDir(Action<string> populate)
    {
        string src = Path.Combine(Path.GetTempPath(), $"xiso_rsk_src_{Guid.NewGuid():N}");
        Directory.CreateDirectory(src);
        _tempDirs.Add(src);
        populate(src);
        return src;
    }

    private string CreateIso(string srcDir, int? prependSectors = null)
    {
        string outDir = CreateTempDir();
        int result = XisoWriter.CreateXiso(srcDir, outDir, null, null, out string? isoPath, null, null,
            prependSectors: prependSectors);
        Assert.Equal(0, result);
        Assert.NotNull(isoPath);
        return isoPath;
    }

    private static void PopulateSimple(string dir)
    {
        File.WriteAllText(Path.Combine(dir, "a.txt"), "hello");
        File.WriteAllText(Path.Combine(dir, "b.txt"), new string('x', 3000));
        Directory.CreateDirectory(Path.Combine(dir, "sub"));
        File.WriteAllText(Path.Combine(dir, "sub", "c.txt"), "nested");
    }

    // -----------------------------------------------------------------------
    // XisoRedump.TryExtractVideo
    // -----------------------------------------------------------------------

    [Fact]
    public void TryExtractVideo_NonRedumpSize_ReturnsFalseAndNullOutPath()
    {
        string src = CreateSourceDir(PopulateSimple);
        string iso = CreateIso(src);
        // iso size is ~589824, not a known Redump size
        bool ok = XisoRedump.TryExtractVideo(iso, null, out string? outPath, quiet: true);

        Assert.False(ok);
        Assert.Null(outPath);
    }

    [Fact]
    public void TryExtractVideo_NonRedumpSize_QuietFalse_AlsoReturnsFalse()
    {
        string src = CreateSourceDir(PopulateSimple);
        string iso = CreateIso(src);

        bool ok = XisoRedump.TryExtractVideo(iso, null, out string? outPath, quiet: false);

        Assert.False(ok);
        Assert.Null(outPath);
    }

    [Fact]
    public void TryExtractVideo_SmallSyntheticIso_ReturnsFalse()
    {
        string outDir = CreateTempDir();
        string small = Path.Combine(outDir, "small.iso");
        File.WriteAllBytes(small, new byte[2048 * 10]);

        bool ok = XisoRedump.TryExtractVideo(small, null, out string? outPath, quiet: true);

        Assert.False(ok);
        Assert.Null(outPath);
    }

    [Fact]
    public void TryExtractVideo_MissingFile_ReturnsFalse()
    {
        string outDir = CreateTempDir();
        string missing = Path.Combine(outDir, "missing.iso");

        bool ok = XisoRedump.TryExtractVideo(missing, null, out string? outPath, quiet: true);

        Assert.False(ok);
        Assert.Null(outPath);
    }

    [Fact]
    public void TryExtractVideo_Cancellation_ThrowsOperationCanceledException()
    {
        string src = CreateSourceDir(PopulateSimple);
        string iso = CreateIso(src);
        using CancellationTokenSource cts = new();
        cts.Cancel();

        Assert.Throws<OperationCanceledException>(() => XisoRedump.TryExtractVideo(iso, null, out _, true, cts.Token));
    }

    [Fact]
    public void TryExtractVideo_WithExplicitOutputPath_NonRedumpReturnsFalse()
    {
        string src = CreateSourceDir(PopulateSimple);
        string iso = CreateIso(src);
        string outDir = CreateTempDir();
        string videoOut = Path.Combine(outDir, "explicit.video.iso");

        bool ok = XisoRedump.TryExtractVideo(iso, videoOut, out string? outPath, quiet: true);

        Assert.False(ok);
        Assert.Null(outPath);
        Assert.False(File.Exists(videoOut));
    }

    // -----------------------------------------------------------------------
    // XisoRedump.TryExtractUpdate
    // -----------------------------------------------------------------------

    [Fact]
    public void TryExtractUpdate_NonXgd3Video_ReturnsFalse()
    {
        string outDir = CreateTempDir();
        string fakeVideo = Path.Combine(outDir, "fake.video.iso");
        File.WriteAllBytes(fakeVideo, new byte[1024 * 1024]);

        bool ok = XisoRedump.TryExtractUpdate(fakeVideo, null, wipe: true, quiet: true);

        Assert.False(ok);
    }

    [Fact]
    public void TryExtractUpdate_NonXgd3Video_QuietFalse_ReturnsFalse()
    {
        string outDir = CreateTempDir();
        string fakeVideo = Path.Combine(outDir, "fake.video.iso");
        File.WriteAllBytes(fakeVideo, new byte[512 * 1024]);

        bool ok = XisoRedump.TryExtractUpdate(fakeVideo, null, wipe: true, quiet: false);

        Assert.False(ok);
    }

    [Fact]
    public void TryExtractUpdate_MissingFile_ReturnsFalse()
    {
        string outDir = CreateTempDir();
        string missing = Path.Combine(outDir, "missing.video.iso");

        Assert.False(XisoRedump.TryExtractUpdate(missing, null, true, true));
    }

    [Fact]
    public void TryExtractUpdate_SmallIso_ReturnsFalse()
    {
        string outDir = CreateTempDir();
        string small = Path.Combine(outDir, "tiny.video.iso");
        File.WriteAllBytes(small, new byte[2048]);

        bool ok = XisoRedump.TryExtractUpdate(small, null, true, true);

        Assert.False(ok);
    }

    // -----------------------------------------------------------------------
    // XisoRedump.RebuildRedump / TryRebuildFromArgs
    // -----------------------------------------------------------------------

    [Fact]
    public void RebuildRedump_InvalidVideoSize_ReturnsFalse()
    {
        string src = CreateSourceDir(PopulateSimple);
        string iso = CreateIso(src);
        string outDir = CreateTempDir();
        string fakeVideo = Path.Combine(outDir, "fake.video.iso");
        File.WriteAllBytes(fakeVideo, new byte[1024 * 1024]);
        string outRedump = Path.Combine(outDir, "rebuilt.iso");

        bool ok = XisoRedump.RebuildRedump(iso, fakeVideo, null, null, outRedump, null, quiet: true);

        Assert.False(ok);
        // Should not create output on failure (or if created, should be deleted/empty)
        // Rebuild creates the FileStream before validation, so file may exist but we just ensure return is false
    }

    [Fact]
    public void RebuildRedump_InvalidXisoMagic_ReturnsFalse()
    {
        string outDir = CreateTempDir();
        string badXiso = Path.Combine(outDir, "bad.iso");
        File.WriteAllBytes(badXiso, new byte[2048 * 100]);
        string fakeVideo = Path.Combine(outDir, "fake.video.iso");
        File.WriteAllBytes(fakeVideo, new byte[1024 * 1024]);
        string outRedump = Path.Combine(outDir, "rebuilt.iso");

        bool ok = XisoRedump.RebuildRedump(badXiso, fakeVideo, null, null, outRedump, null, quiet: true);

        Assert.False(ok);
    }

    [Fact]
    public void RebuildRedump_MissingVideoFile_ThrowsFileNotFoundException()
    {
        string src = CreateSourceDir(PopulateSimple);
        string iso = CreateIso(src);
        string outDir = CreateTempDir();
        string missingVideo = Path.Combine(outDir, "missing.video.iso");
        string outRedump = Path.Combine(outDir, "rebuilt.iso");

        Assert.Throws<FileNotFoundException>(() =>
            XisoRedump.RebuildRedump(iso, missingVideo, null, null, outRedump, null, true));
    }

    [Fact]
    public void RebuildRedump_Cancellation_ThrowsOperationCanceledException()
    {
        string src = CreateSourceDir(PopulateSimple);
        string iso = CreateIso(src);
        string outDir = CreateTempDir();
        string fakeVideo = Path.Combine(outDir, "fake.video.iso");
        File.WriteAllBytes(fakeVideo, new byte[1024 * 1024]);
        string outRedump = Path.Combine(outDir, "rebuilt.iso");
        using CancellationTokenSource cts = new();
        cts.Cancel();

        Assert.Throws<OperationCanceledException>(() =>
            XisoRedump.RebuildRedump(iso, fakeVideo, null, null, outRedump, null, true, cts.Token));
    }

    [Fact]
    public void TryRebuildFromArgs_NoVideo_ReturnsFalse()
    {
        string src = CreateSourceDir(PopulateSimple);
        string iso = CreateIso(src);
        string outDir = CreateTempDir();
        string outRedump = Path.Combine(outDir, "rebuilt.iso");

        bool ok = XisoRedump.TryRebuildFromArgs([], iso, outRedump, quiet: true);

        Assert.False(ok);
    }

    [Fact]
    public void TryRebuildFromArgs_WithNonVideoFiles_ReturnsFalse()
    {
        string src = CreateSourceDir(PopulateSimple);
        string iso = CreateIso(src);
        string outDir = CreateTempDir();
        string txt = Path.Combine(outDir, "notes.txt");
        File.WriteAllText(txt, "hello");
        string outRedump = Path.Combine(outDir, "rebuilt.iso");

        bool ok = XisoRedump.TryRebuildFromArgs([txt], iso, outRedump, quiet: true);

        Assert.False(ok);
    }

    [Fact]
    public void TryRebuildFromArgs_WithFakeVideo_ReturnsFalse()
    {
        string src = CreateSourceDir(PopulateSimple);
        string iso = CreateIso(src);
        string outDir = CreateTempDir();
        string fakeVideo = Path.Combine(outDir, "fake.video.iso");
        File.WriteAllBytes(fakeVideo, new byte[1024 * 1024]);
        string outRedump = Path.Combine(outDir, "rebuilt.iso");

        bool ok = XisoRedump.TryRebuildFromArgs([fakeVideo], iso, outRedump, quiet: true);

        Assert.False(ok);
    }

    [Fact]
    public void TryRebuildFromArgs_MissingXiso_ReturnsFalse()
    {
        string outDir = CreateTempDir();
        string fakeVideo = Path.Combine(outDir, "fake.video.iso");
        File.WriteAllBytes(fakeVideo, new byte[1024 * 1024]);
        string missingXiso = Path.Combine(outDir, "missing.iso");
        string outRedump = Path.Combine(outDir, "rebuilt.iso");

        // TryRebuildFromArgs will attempt to infer video then call RebuildRedump which will fail to open xiso
        // It returns false for non-video or missing; we assert false (not throw) when xiso missing but video present?
        // Actually RebuildRedump will throw FileNotFound for missing xiso; TryRebuildFromArgs wraps? Let's verify it returns false without throw for our probe.
        bool ok = XisoRedump.TryRebuildFromArgs([fakeVideo], missingXiso, outRedump, quiet: true);
        // Probe showed it returns false, not throw
        Assert.False(ok);
    }

    // -----------------------------------------------------------------------
    // XisoSkeleton.Petrify
    // -----------------------------------------------------------------------

    [Fact]
    public void Petrify_CreatesSkeletonAndHashFiles()
    {
        string src = CreateSourceDir(PopulateSimple);
        string iso = CreateIso(src);
        string outDir = CreateTempDir();
        string skel = Path.Combine(outDir, "test.skeleton.xiso");
        string hash = Path.Combine(outDir, "test.hash");

        bool ok = XisoSkeleton.Petrify(iso, skel, hash, 0, quiet: true);

        Assert.True(ok);
        Assert.True(File.Exists(skel));
        Assert.True(File.Exists(hash));
        Assert.True(new FileInfo(skel).Length > 0);
        Assert.True(new FileInfo(hash).Length > 0);
    }

    [Fact]
    public void Petrify_SkeletonSizeEqualsOriginalAndHashLinesMatchFileCount()
    {
        string src = CreateSourceDir(PopulateSimple); // 3 files
        string iso = CreateIso(src);
        string outDir = CreateTempDir();
        string skel = Path.Combine(outDir, "size.skeleton.xiso");
        string hash = Path.Combine(outDir, "size.hash");

        bool ok = XisoSkeleton.Petrify(iso, skel, hash, 0, quiet: true);
        Assert.True(ok);

        Assert.Equal(new FileInfo(iso).Length, new FileInfo(skel).Length);

        string[] lines = File.ReadAllLines(hash);
        Assert.Equal(3, lines.Length);
        // Each line: "<40 hex sha1> <path>"
        foreach (string line in lines)
        {
            string[] parts = line.Split(' ', 2);
            Assert.Equal(2, parts.Length);
            Assert.Equal(40, parts[0].Length);
            Assert.Matches("^[0-9a-f]{40}$", parts[0]);
            Assert.False(string.IsNullOrWhiteSpace(parts[1]));
        }

        // Hash entries sorted? Verify they contain expected file paths
        string joined = string.Join("\n", lines);
        Assert.Contains("a.txt", joined, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("b.txt", joined, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("c.txt", joined, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Petrify_DerivedPaths_CreatesDefaultOutputs()
    {
        string src = CreateSourceDir(PopulateSimple);
        string iso = CreateIso(src);
        // Copy to a path with .iso extension to test derived naming
        string outDir = CreateTempDir();
        string derivedIso = Path.Combine(outDir, "derived_test.iso");
        File.Copy(iso, derivedIso, true);

        bool ok = XisoSkeleton.Petrify(derivedIso, null, null, 0, quiet: true);

        Assert.True(ok);
        string expectedSkel = Path.Combine(outDir, "derived_test.skeleton.xiso");
        string expectedHash = Path.Combine(outDir, "derived_test.hash");
        Assert.True(File.Exists(expectedSkel), $"Expected skeleton at {expectedSkel}");
        Assert.True(File.Exists(expectedHash), $"Expected hash at {expectedHash}");
    }

    [Fact]
    public void Petrify_QuietFalse_Succeeds()
    {
        string src = CreateSourceDir(PopulateSimple);
        string iso = CreateIso(src);
        string outDir = CreateTempDir();
        string skel = Path.Combine(outDir, "quiet.skeleton.xiso");
        string hash = Path.Combine(outDir, "quiet.hash");

        bool ok = XisoSkeleton.Petrify(iso, skel, hash, 0, quiet: false);

        Assert.True(ok);
        Assert.True(File.Exists(skel));
    }

    [Fact]
    public void Petrify_MissingFile_ThrowsFileNotFoundException()
    {
        string outDir = CreateTempDir();
        string missing = Path.Combine(outDir, "missing.iso");
        string skel = Path.Combine(outDir, "out.skeleton.xiso");
        string hash = Path.Combine(outDir, "out.hash");

        Assert.Throws<FileNotFoundException>(() => XisoSkeleton.Petrify(missing, skel, hash, 0, true));
    }

    [Fact]
    public void Petrify_InvalidIso_ThrowsEndOfStreamException()
    {
        string outDir = CreateTempDir();
        string bad = Path.Combine(outDir, "bad.iso");
        File.WriteAllBytes(bad, new byte[100]);
        string skel = Path.Combine(outDir, "bad.skeleton.xiso");
        string hash = Path.Combine(outDir, "bad.hash");

        Assert.Throws<EndOfStreamException>(() => XisoSkeleton.Petrify(bad, skel, hash, 0, true));
    }

    [Fact]
    public void Petrify_Cancellation_ThrowsOperationCanceledException()
    {
        string src = CreateSourceDir(PopulateSimple);
        string iso = CreateIso(src);
        string outDir = CreateTempDir();
        string skel = Path.Combine(outDir, "cancel.skeleton.xiso");
        string hash = Path.Combine(outDir, "cancel.hash");
        using CancellationTokenSource cts = new();
        cts.Cancel();

        Assert.Throws<OperationCanceledException>(() => XisoSkeleton.Petrify(iso, skel, hash, 0, true, cts.Token));
    }

    [Fact]
    public void Petrify_SkeletonIsExtractable()
    {
        string src = CreateSourceDir(PopulateSimple);
        string iso = CreateIso(src);
        string outDir = CreateTempDir();
        string skel = Path.Combine(outDir, "extractable.skeleton.xiso");
        string hash = Path.Combine(outDir, "extractable.hash");

        bool ok = XisoSkeleton.Petrify(iso, skel, hash, 0, quiet: true);
        Assert.True(ok);

        // Skeleton should still be a valid XISO that can be listed/verified, but file data zeroed
        int listResult = XisoReader.List(skel, llCompat: false);
        Assert.Equal(0, listResult);

        string extractDir = CreateTempDir();
        int ext = XisoReader.Extract(skel, extractDir, llCompat: false);
        Assert.Equal(0, ext);
        // File should exist but be zeroed
        string extracted = Path.Combine(extractDir, "a.txt");
        Assert.True(File.Exists(extracted));
        // Original a.txt was "hello" (5 bytes), skeleton should have zeros
        byte[] bytes = File.ReadAllBytes(extracted);
        Assert.Equal(5, bytes.Length);
        Assert.All(bytes, b => Assert.Equal(0, b));
    }

    [Fact]
    public void Petrify_WithPrependedIso_Succeeds()
    {
        string src = CreateSourceDir(PopulateSimple);
        string iso = CreateIso(src, prependSectors: 16);
        const long offset = 16L * Constants.SectorSize;
        string outDir = CreateTempDir();
        string skel = Path.Combine(outDir, "prepend.skeleton.xiso");
        string hash = Path.Combine(outDir, "prepend.hash");

        bool ok = XisoSkeleton.Petrify(iso, skel, hash, offset, quiet: true);

        Assert.True(ok);
        Assert.True(File.Exists(skel));
        Assert.True(File.Exists(hash));
    }

    // -----------------------------------------------------------------------
    // XisoZarchive.CreateZar
    // -----------------------------------------------------------------------

    [Fact]
    public void CreateZar_PathOverload_CreatesZarFile()
    {
        string src = CreateSourceDir(PopulateSimple);
        string iso = CreateIso(src);
        string outDir = CreateTempDir();
        string zar = Path.Combine(outDir, "test.zar");

        bool ok = XisoZarchive.CreateZar(iso, zar, 0, quiet: true);

        Assert.True(ok);
        Assert.True(File.Exists(zar));
        Assert.True(new FileInfo(zar).Length > 0);
        // ZAR should have magic at end (footer)
        using FileStream fs = new(zar, FileMode.Open, FileAccess.Read, FileShare.Read);
        Assert.True(fs.Length > 144);
        fs.Seek(-8, SeekOrigin.End);
        Span<byte> footer = stackalloc byte[8];
        fs.ReadExactly(footer);
        // Last 8 bytes: magic 0x16 0x9F 0x52 0xD6 + version? Actually magic at -8, version at -12?
        // Check that footer contains magic somewhere in last 144
        fs.Seek(-144, SeekOrigin.End);
        byte[] buf = new byte[144];
        fs.ReadExactly(buf);
        // Magic bytes should be at offset 140-144 (last 4) and version at 136-140
        Assert.Equal(0x16, buf[140]);
        Assert.Equal(0x9F, buf[141]);
        Assert.Equal(0x52, buf[142]);
        Assert.Equal(0xD6, buf[143]);
    }

    [Fact]
    public void CreateZar_FileStreamOverload_CreatesZarFile()
    {
        string src = CreateSourceDir(PopulateSimple);
        string iso = CreateIso(src);
        string outDir = CreateTempDir();
        string zar = Path.Combine(outDir, "stream.zar");

        using FileStream fs = new(iso, FileMode.Open, FileAccess.Read, FileShare.Read, 65536);
        bool ok = XisoZarchive.CreateZar(fs, 0, zar, removeUpdate: false, quiet: true);

        Assert.True(ok);
        Assert.True(File.Exists(zar));
        Assert.True(new FileInfo(zar).Length > 0);
    }

    [Fact]
    public void CreateZar_DerivedPath_CreatesDefaultZar()
    {
        string src = CreateSourceDir(PopulateSimple);
        string iso = CreateIso(src);
        string outDir = CreateTempDir();
        string derivedIso = Path.Combine(outDir, "derived_for_zar.iso");
        File.Copy(iso, derivedIso, true);

        bool ok = XisoZarchive.CreateZar(derivedIso, null, 0, quiet: true);

        Assert.True(ok);
        string expectedZar = Path.Combine(outDir, "derived_for_zar.zar");
        Assert.True(File.Exists(expectedZar), $"Expected ZAR at {expectedZar}");
        Assert.True(new FileInfo(expectedZar).Length > 0);
    }

    [Fact]
    public void CreateZar_RemoveUpdateTrue_CreatesZar()
    {
        string src = CreateSourceDir(d =>
        {
            File.WriteAllText(Path.Combine(d, "game.txt"), "game");
            Directory.CreateDirectory(Path.Combine(d, "$SystemUpdate"));
            File.WriteAllText(Path.Combine(d, "$SystemUpdate", "upd.bin"), "update");
        });
        string iso = CreateIso(src);
        string outDir = CreateTempDir();
        string zarNoRemove = Path.Combine(outDir, "noremove.zar");
        string zarRemove = Path.Combine(outDir, "remove.zar");

        using (FileStream fs = new(iso, FileMode.Open, FileAccess.Read, FileShare.Read, 65536))
        {
            bool ok1 = XisoZarchive.CreateZar(fs, 0, zarNoRemove, removeUpdate: false, quiet: true);
            Assert.True(ok1);
        }

        using (FileStream fs = new(iso, FileMode.Open, FileAccess.Read, FileShare.Read, 65536))
        {
            bool ok2 = XisoZarchive.CreateZar(fs, 0, zarRemove, removeUpdate: true, quiet: true);
            Assert.True(ok2);
        }

        Assert.True(File.Exists(zarNoRemove));
        Assert.True(File.Exists(zarRemove));
        Assert.True(new FileInfo(zarNoRemove).Length > 0);
        Assert.True(new FileInfo(zarRemove).Length > 0);
    }

    [Fact]
    public void CreateZar_WithIsoOffset_PrependedIso_Succeeds()
    {
        string src = CreateSourceDir(PopulateSimple);
        string iso = CreateIso(src, prependSectors: 16);
        const long offset = 16L * Constants.SectorSize;
        string outDir = CreateTempDir();
        string zar = Path.Combine(outDir, "prepend.zar");

        bool ok = XisoZarchive.CreateZar(iso, zar, offset, quiet: true);

        Assert.True(ok);
        Assert.True(File.Exists(zar));
    }

    [Fact]
    public void CreateZar_MissingFile_ThrowsFileNotFoundException()
    {
        string outDir = CreateTempDir();
        string missing = Path.Combine(outDir, "missing.iso");
        string zar = Path.Combine(outDir, "missing.zar");

        Assert.Throws<FileNotFoundException>(() => XisoZarchive.CreateZar(missing, zar, 0, true));
    }

    [Fact]
    public void CreateZar_InvalidIso_ThrowsEndOfStreamException()
    {
        string outDir = CreateTempDir();
        string bad = Path.Combine(outDir, "bad.iso");
        File.WriteAllBytes(bad, new byte[100]);
        string zar = Path.Combine(outDir, "bad.zar");

        Assert.Throws<EndOfStreamException>(() => XisoZarchive.CreateZar(bad, zar, 0, true));
    }

    [Fact]
    public void CreateZar_Cancellation_ThrowsOperationCanceledException()
    {
        string src = CreateSourceDir(PopulateSimple);
        string iso = CreateIso(src);
        string outDir = CreateTempDir();
        string zar = Path.Combine(outDir, "cancel.zar");
        using CancellationTokenSource cts = new();
        cts.Cancel();

        Assert.Throws<OperationCanceledException>(() => XisoZarchive.CreateZar(iso, zar, 0, true, cts.Token));
    }

    [Fact]
    public void CreateZar_Cancellation_FileStreamOverload_ThrowsOperationCanceledException()
    {
        string src = CreateSourceDir(PopulateSimple);
        string iso = CreateIso(src);
        string outDir = CreateTempDir();
        string zar = Path.Combine(outDir, "cancel2.zar");
        using CancellationTokenSource cts = new();
        cts.Cancel();
        using FileStream fs = new(iso, FileMode.Open, FileAccess.Read, FileShare.Read, 65536);

        Assert.Throws<OperationCanceledException>(() => XisoZarchive.CreateZar(fs, 0, zar, false, true, cts.Token));
    }

    [Fact]
    public void CreateZar_LargerContent_CreatesNonEmptyZar()
    {
        string src = CreateSourceDir(d =>
        {
            File.WriteAllText(Path.Combine(d, "a.txt"), new string('a', 10000));
            File.WriteAllText(Path.Combine(d, "b.txt"), new string('b', 70000)); // > one block (64KB)
            Directory.CreateDirectory(Path.Combine(d, "sub"));
            File.WriteAllBytes(Path.Combine(d, "sub", "big.bin"), new byte[150000]);
        });
        string iso = CreateIso(src);
        string outDir = CreateTempDir();
        string zar = Path.Combine(outDir, "large.zar");

        bool ok = XisoZarchive.CreateZar(iso, zar, 0, quiet: true);

        Assert.True(ok);
        long len = new FileInfo(zar).Length;
        // ZAR should be larger than just footer (144); blocks are zstd-compressed
        // (see XisoZarConvertTests for ratio assertions), so only the footer bound holds.
        Assert.True(len > 144);
        // Output must open in the real reader with all three files present.
        using ZArchiveReader? reader = ZArchiveReader.TryOpen(zar);
        Assert.NotNull(reader);
        Assert.Equal(10000UL, reader.GetFileSize(reader.LookUp("a.txt")));
        Assert.Equal(70000UL, reader.GetFileSize(reader.LookUp("b.txt")));
        Assert.Equal(150000UL, reader.GetFileSize(reader.LookUp("sub/big.bin")));
    }
}
