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
    }

    private string CreateTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"xiso_rsk_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        _tempDirs.Add(dir);
        return dir;
    }

    private string CreateSourceDir(Action<string> populate)
    {
        var src = Path.Combine(Path.GetTempPath(), $"xiso_rsk_src_{Guid.NewGuid():N}");
        Directory.CreateDirectory(src);
        _tempDirs.Add(src);
        populate(src);
        return src;
    }

    private string CreateIso(string srcDir, int? prependSectors = null)
    {
        var outDir = CreateTempDir();
        var result = XisoWriter.CreateXiso(srcDir, outDir, null, null, out var isoPath, null, null,
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
        var src = CreateSourceDir(PopulateSimple);
        var iso = CreateIso(src);
        // iso size is ~589824, not a known Redump size
        var ok = XisoRedump.TryExtractVideo(iso, null, out var outPath, quiet: true);

        Assert.False(ok);
        Assert.Null(outPath);
    }

    [Fact]
    public void TryExtractVideo_NonRedumpSize_QuietFalse_AlsoReturnsFalse()
    {
        var src = CreateSourceDir(PopulateSimple);
        var iso = CreateIso(src);

        var ok = XisoRedump.TryExtractVideo(iso, null, out var outPath, quiet: false);

        Assert.False(ok);
        Assert.Null(outPath);
    }

    [Fact]
    public void TryExtractVideo_SmallSyntheticIso_ReturnsFalse()
    {
        var outDir = CreateTempDir();
        var small = Path.Combine(outDir, "small.iso");
        File.WriteAllBytes(small, new byte[2048 * 10]);

        var ok = XisoRedump.TryExtractVideo(small, null, out var outPath, quiet: true);

        Assert.False(ok);
        Assert.Null(outPath);
    }

    [Fact]
    public void TryExtractVideo_MissingFile_ThrowsFileNotFoundException()
    {
        var outDir = CreateTempDir();
        var missing = Path.Combine(outDir, "missing.iso");

        Assert.Throws<FileNotFoundException>(() => XisoRedump.TryExtractVideo(missing, null, out _, quiet: true));
    }

    [Fact]
    public void TryExtractVideo_Cancellation_ThrowsOperationCanceledException()
    {
        var src = CreateSourceDir(PopulateSimple);
        var iso = CreateIso(src);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.Throws<OperationCanceledException>(() => XisoRedump.TryExtractVideo(iso, null, out _, true, cts.Token));
    }

    [Fact]
    public void TryExtractVideo_WithExplicitOutputPath_NonRedumpReturnsFalse()
    {
        var src = CreateSourceDir(PopulateSimple);
        var iso = CreateIso(src);
        var outDir = CreateTempDir();
        var videoOut = Path.Combine(outDir, "explicit.video.iso");

        var ok = XisoRedump.TryExtractVideo(iso, videoOut, out var outPath, quiet: true);

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
        var outDir = CreateTempDir();
        var fakeVideo = Path.Combine(outDir, "fake.video.iso");
        File.WriteAllBytes(fakeVideo, new byte[1024 * 1024]);

        var ok = XisoRedump.TryExtractUpdate(fakeVideo, null, wipe: true, quiet: true);

        Assert.False(ok);
    }

    [Fact]
    public void TryExtractUpdate_NonXgd3Video_QuietFalse_ReturnsFalse()
    {
        var outDir = CreateTempDir();
        var fakeVideo = Path.Combine(outDir, "fake.video.iso");
        File.WriteAllBytes(fakeVideo, new byte[512 * 1024]);

        var ok = XisoRedump.TryExtractUpdate(fakeVideo, null, wipe: true, quiet: false);

        Assert.False(ok);
    }

    [Fact]
    public void TryExtractUpdate_MissingFile_ThrowsFileNotFoundException()
    {
        var outDir = CreateTempDir();
        var missing = Path.Combine(outDir, "missing.video.iso");

        Assert.Throws<FileNotFoundException>(() => XisoRedump.TryExtractUpdate(missing, null, true, true));
    }

    [Fact]
    public void TryExtractUpdate_SmallIso_ReturnsFalse()
    {
        var outDir = CreateTempDir();
        var small = Path.Combine(outDir, "tiny.video.iso");
        File.WriteAllBytes(small, new byte[2048]);

        var ok = XisoRedump.TryExtractUpdate(small, null, true, true);

        Assert.False(ok);
    }

    // -----------------------------------------------------------------------
    // XisoRedump.RebuildRedump / TryRebuildFromArgs
    // -----------------------------------------------------------------------

    [Fact]
    public void RebuildRedump_InvalidVideoSize_ReturnsFalse()
    {
        var src = CreateSourceDir(PopulateSimple);
        var iso = CreateIso(src);
        var outDir = CreateTempDir();
        var fakeVideo = Path.Combine(outDir, "fake.video.iso");
        File.WriteAllBytes(fakeVideo, new byte[1024 * 1024]);
        var outRedump = Path.Combine(outDir, "rebuilt.iso");

        var ok = XisoRedump.RebuildRedump(iso, fakeVideo, null, null, outRedump, null, quiet: true);

        Assert.False(ok);
        // Should not create output on failure (or if created, should be deleted/empty)
        // Rebuild creates the FileStream before validation, so file may exist but we just ensure return is false
    }

    [Fact]
    public void RebuildRedump_InvalidXisoMagic_ReturnsFalse()
    {
        var outDir = CreateTempDir();
        var badXiso = Path.Combine(outDir, "bad.iso");
        File.WriteAllBytes(badXiso, new byte[2048 * 100]);
        var fakeVideo = Path.Combine(outDir, "fake.video.iso");
        File.WriteAllBytes(fakeVideo, new byte[1024 * 1024]);
        var outRedump = Path.Combine(outDir, "rebuilt.iso");

        var ok = XisoRedump.RebuildRedump(badXiso, fakeVideo, null, null, outRedump, null, quiet: true);

        Assert.False(ok);
    }

    [Fact]
    public void RebuildRedump_MissingVideoFile_ThrowsFileNotFoundException()
    {
        var src = CreateSourceDir(PopulateSimple);
        var iso = CreateIso(src);
        var outDir = CreateTempDir();
        var missingVideo = Path.Combine(outDir, "missing.video.iso");
        var outRedump = Path.Combine(outDir, "rebuilt.iso");

        Assert.Throws<FileNotFoundException>(() =>
            XisoRedump.RebuildRedump(iso, missingVideo, null, null, outRedump, null, true));
    }

    [Fact]
    public void RebuildRedump_Cancellation_ThrowsOperationCanceledException()
    {
        var src = CreateSourceDir(PopulateSimple);
        var iso = CreateIso(src);
        var outDir = CreateTempDir();
        var fakeVideo = Path.Combine(outDir, "fake.video.iso");
        File.WriteAllBytes(fakeVideo, new byte[1024 * 1024]);
        var outRedump = Path.Combine(outDir, "rebuilt.iso");
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.Throws<OperationCanceledException>(() =>
            XisoRedump.RebuildRedump(iso, fakeVideo, null, null, outRedump, null, true, cts.Token));
    }

    [Fact]
    public void TryRebuildFromArgs_NoVideo_ReturnsFalse()
    {
        var src = CreateSourceDir(PopulateSimple);
        var iso = CreateIso(src);
        var outDir = CreateTempDir();
        var outRedump = Path.Combine(outDir, "rebuilt.iso");

        var ok = XisoRedump.TryRebuildFromArgs([], iso, outRedump, quiet: true);

        Assert.False(ok);
    }

    [Fact]
    public void TryRebuildFromArgs_WithNonVideoFiles_ReturnsFalse()
    {
        var src = CreateSourceDir(PopulateSimple);
        var iso = CreateIso(src);
        var outDir = CreateTempDir();
        var txt = Path.Combine(outDir, "notes.txt");
        File.WriteAllText(txt, "hello");
        var outRedump = Path.Combine(outDir, "rebuilt.iso");

        var ok = XisoRedump.TryRebuildFromArgs([txt], iso, outRedump, quiet: true);

        Assert.False(ok);
    }

    [Fact]
    public void TryRebuildFromArgs_WithFakeVideo_ReturnsFalse()
    {
        var src = CreateSourceDir(PopulateSimple);
        var iso = CreateIso(src);
        var outDir = CreateTempDir();
        var fakeVideo = Path.Combine(outDir, "fake.video.iso");
        File.WriteAllBytes(fakeVideo, new byte[1024 * 1024]);
        var outRedump = Path.Combine(outDir, "rebuilt.iso");

        var ok = XisoRedump.TryRebuildFromArgs([fakeVideo], iso, outRedump, quiet: true);

        Assert.False(ok);
    }

    [Fact]
    public void TryRebuildFromArgs_MissingXiso_ReturnsFalse()
    {
        var outDir = CreateTempDir();
        var fakeVideo = Path.Combine(outDir, "fake.video.iso");
        File.WriteAllBytes(fakeVideo, new byte[1024 * 1024]);
        var missingXiso = Path.Combine(outDir, "missing.iso");
        var outRedump = Path.Combine(outDir, "rebuilt.iso");

        // TryRebuildFromArgs will attempt to infer video then call RebuildRedump which will fail to open xiso
        // It returns false for non-video or missing; we assert false (not throw) when xiso missing but video present?
        // Actually RebuildRedump will throw FileNotFound for missing xiso; TryRebuildFromArgs wraps? Let's verify it returns false without throw for our probe.
        var ok = XisoRedump.TryRebuildFromArgs([fakeVideo], missingXiso, outRedump, quiet: true);
        // Probe showed it returns false, not throw
        Assert.False(ok);
    }

    // -----------------------------------------------------------------------
    // XisoSkeleton.Petrify
    // -----------------------------------------------------------------------

    [Fact]
    public void Petrify_CreatesSkeletonAndHashFiles()
    {
        var src = CreateSourceDir(PopulateSimple);
        var iso = CreateIso(src);
        var outDir = CreateTempDir();
        var skel = Path.Combine(outDir, "test.skeleton.xiso");
        var hash = Path.Combine(outDir, "test.hash");

        var ok = XisoSkeleton.Petrify(iso, skel, hash, 0, quiet: true);

        Assert.True(ok);
        Assert.True(File.Exists(skel));
        Assert.True(File.Exists(hash));
        Assert.True(new FileInfo(skel).Length > 0);
        Assert.True(new FileInfo(hash).Length > 0);
    }

    [Fact]
    public void Petrify_SkeletonSizeEqualsOriginalAndHashLinesMatchFileCount()
    {
        var src = CreateSourceDir(PopulateSimple); // 3 files
        var iso = CreateIso(src);
        var outDir = CreateTempDir();
        var skel = Path.Combine(outDir, "size.skeleton.xiso");
        var hash = Path.Combine(outDir, "size.hash");

        var ok = XisoSkeleton.Petrify(iso, skel, hash, 0, quiet: true);
        Assert.True(ok);

        Assert.Equal(new FileInfo(iso).Length, new FileInfo(skel).Length);

        var lines = File.ReadAllLines(hash);
        Assert.Equal(3, lines.Length);
        // Each line: "<40 hex sha1> <path>"
        foreach (var line in lines)
        {
            var parts = line.Split(' ', 2);
            Assert.Equal(2, parts.Length);
            Assert.Equal(40, parts[0].Length);
            Assert.Matches("^[0-9a-f]{40}$", parts[0]);
            Assert.False(string.IsNullOrWhiteSpace(parts[1]));
        }

        // Hash entries sorted? Verify they contain expected file paths
        var joined = string.Join("\n", lines);
        Assert.Contains("a.txt", joined, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("b.txt", joined, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("c.txt", joined, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Petrify_DerivedPaths_CreatesDefaultOutputs()
    {
        var src = CreateSourceDir(PopulateSimple);
        var iso = CreateIso(src);
        // Copy to a path with .iso extension to test derived naming
        var outDir = CreateTempDir();
        var derivedIso = Path.Combine(outDir, "derived_test.iso");
        File.Copy(iso, derivedIso, true);

        var ok = XisoSkeleton.Petrify(derivedIso, null, null, 0, quiet: true);

        Assert.True(ok);
        var expectedSkel = Path.Combine(outDir, "derived_test.skeleton.xiso");
        var expectedHash = Path.Combine(outDir, "derived_test.hash");
        Assert.True(File.Exists(expectedSkel), $"Expected skeleton at {expectedSkel}");
        Assert.True(File.Exists(expectedHash), $"Expected hash at {expectedHash}");
    }

    [Fact]
    public void Petrify_QuietFalse_Succeeds()
    {
        var src = CreateSourceDir(PopulateSimple);
        var iso = CreateIso(src);
        var outDir = CreateTempDir();
        var skel = Path.Combine(outDir, "quiet.skeleton.xiso");
        var hash = Path.Combine(outDir, "quiet.hash");

        var ok = XisoSkeleton.Petrify(iso, skel, hash, 0, quiet: false);

        Assert.True(ok);
        Assert.True(File.Exists(skel));
    }

    [Fact]
    public void Petrify_MissingFile_ThrowsFileNotFoundException()
    {
        var outDir = CreateTempDir();
        var missing = Path.Combine(outDir, "missing.iso");
        var skel = Path.Combine(outDir, "out.skeleton.xiso");
        var hash = Path.Combine(outDir, "out.hash");

        Assert.Throws<FileNotFoundException>(() => XisoSkeleton.Petrify(missing, skel, hash, 0, true));
    }

    [Fact]
    public void Petrify_InvalidIso_ThrowsEndOfStreamException()
    {
        var outDir = CreateTempDir();
        var bad = Path.Combine(outDir, "bad.iso");
        File.WriteAllBytes(bad, new byte[100]);
        var skel = Path.Combine(outDir, "bad.skeleton.xiso");
        var hash = Path.Combine(outDir, "bad.hash");

        Assert.Throws<EndOfStreamException>(() => XisoSkeleton.Petrify(bad, skel, hash, 0, true));
    }

    [Fact]
    public void Petrify_Cancellation_ThrowsOperationCanceledException()
    {
        var src = CreateSourceDir(PopulateSimple);
        var iso = CreateIso(src);
        var outDir = CreateTempDir();
        var skel = Path.Combine(outDir, "cancel.skeleton.xiso");
        var hash = Path.Combine(outDir, "cancel.hash");
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.Throws<OperationCanceledException>(() => XisoSkeleton.Petrify(iso, skel, hash, 0, true, cts.Token));
    }

    [Fact]
    public void Petrify_SkeletonIsExtractable()
    {
        var src = CreateSourceDir(PopulateSimple);
        var iso = CreateIso(src);
        var outDir = CreateTempDir();
        var skel = Path.Combine(outDir, "extractable.skeleton.xiso");
        var hash = Path.Combine(outDir, "extractable.hash");

        var ok = XisoSkeleton.Petrify(iso, skel, hash, 0, quiet: true);
        Assert.True(ok);

        // Skeleton should still be a valid XISO that can be listed/verified, but file data zeroed
        var listResult = XisoReader.List(skel, llCompat: false);
        Assert.Equal(0, listResult);

        var extractDir = CreateTempDir();
        var ext = XisoReader.Extract(skel, extractDir, llCompat: false);
        Assert.Equal(0, ext);
        // File should exist but be zeroed
        var extracted = Path.Combine(extractDir, "a.txt");
        Assert.True(File.Exists(extracted));
        // Original a.txt was "hello" (5 bytes), skeleton should have zeros
        var bytes = File.ReadAllBytes(extracted);
        Assert.Equal(5, bytes.Length);
        Assert.All(bytes, b => Assert.Equal(0, b));
    }

    [Fact]
    public void Petrify_WithPrependedIso_Succeeds()
    {
        var src = CreateSourceDir(PopulateSimple);
        var iso = CreateIso(src, prependSectors: 16);
        const long offset = 16L * Constants.SectorSize;
        var outDir = CreateTempDir();
        var skel = Path.Combine(outDir, "prepend.skeleton.xiso");
        var hash = Path.Combine(outDir, "prepend.hash");

        var ok = XisoSkeleton.Petrify(iso, skel, hash, offset, quiet: true);

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
        var src = CreateSourceDir(PopulateSimple);
        var iso = CreateIso(src);
        var outDir = CreateTempDir();
        var zar = Path.Combine(outDir, "test.zar");

        var ok = XisoZarchive.CreateZar(iso, zar, 0, quiet: true);

        Assert.True(ok);
        Assert.True(File.Exists(zar));
        Assert.True(new FileInfo(zar).Length > 0);
        // ZAR should have magic at end (footer)
        using var fs = new FileStream(zar, FileMode.Open, FileAccess.Read, FileShare.Read);
        Assert.True(fs.Length > 144);
        fs.Seek(-8, SeekOrigin.End);
        Span<byte> footer = stackalloc byte[8];
        fs.ReadExactly(footer);
        // Last 8 bytes: magic 0x16 0x9F 0x52 0xD6 + version? Actually magic at -8, version at -12?
        // Check that footer contains magic somewhere in last 144
        fs.Seek(-144, SeekOrigin.End);
        var buf = new byte[144];
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
        var src = CreateSourceDir(PopulateSimple);
        var iso = CreateIso(src);
        var outDir = CreateTempDir();
        var zar = Path.Combine(outDir, "stream.zar");

        using var fs = new FileStream(iso, FileMode.Open, FileAccess.Read, FileShare.Read, 65536);
        var ok = XisoZarchive.CreateZar(fs, 0, zar, removeUpdate: false, quiet: true);

        Assert.True(ok);
        Assert.True(File.Exists(zar));
        Assert.True(new FileInfo(zar).Length > 0);
    }

    [Fact]
    public void CreateZar_DerivedPath_CreatesDefaultZar()
    {
        var src = CreateSourceDir(PopulateSimple);
        var iso = CreateIso(src);
        var outDir = CreateTempDir();
        var derivedIso = Path.Combine(outDir, "derived_for_zar.iso");
        File.Copy(iso, derivedIso, true);

        var ok = XisoZarchive.CreateZar(derivedIso, null, 0, quiet: true);

        Assert.True(ok);
        var expectedZar = Path.Combine(outDir, "derived_for_zar.zar");
        Assert.True(File.Exists(expectedZar), $"Expected ZAR at {expectedZar}");
        Assert.True(new FileInfo(expectedZar).Length > 0);
    }

    [Fact]
    public void CreateZar_RemoveUpdateTrue_CreatesZar()
    {
        var src = CreateSourceDir(d =>
        {
            File.WriteAllText(Path.Combine(d, "game.txt"), "game");
            Directory.CreateDirectory(Path.Combine(d, "$SystemUpdate"));
            File.WriteAllText(Path.Combine(d, "$SystemUpdate", "upd.bin"), "update");
        });
        var iso = CreateIso(src);
        var outDir = CreateTempDir();
        var zarNoRemove = Path.Combine(outDir, "noremove.zar");
        var zarRemove = Path.Combine(outDir, "remove.zar");

        using (var fs = new FileStream(iso, FileMode.Open, FileAccess.Read, FileShare.Read, 65536))
        {
            var ok1 = XisoZarchive.CreateZar(fs, 0, zarNoRemove, removeUpdate: false, quiet: true);
            Assert.True(ok1);
        }

        using (var fs = new FileStream(iso, FileMode.Open, FileAccess.Read, FileShare.Read, 65536))
        {
            var ok2 = XisoZarchive.CreateZar(fs, 0, zarRemove, removeUpdate: true, quiet: true);
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
        var src = CreateSourceDir(PopulateSimple);
        var iso = CreateIso(src, prependSectors: 16);
        const long offset = 16L * Constants.SectorSize;
        var outDir = CreateTempDir();
        var zar = Path.Combine(outDir, "prepend.zar");

        var ok = XisoZarchive.CreateZar(iso, zar, offset, quiet: true);

        Assert.True(ok);
        Assert.True(File.Exists(zar));
    }

    [Fact]
    public void CreateZar_MissingFile_ThrowsFileNotFoundException()
    {
        var outDir = CreateTempDir();
        var missing = Path.Combine(outDir, "missing.iso");
        var zar = Path.Combine(outDir, "missing.zar");

        Assert.Throws<FileNotFoundException>(() => XisoZarchive.CreateZar(missing, zar, 0, true));
    }

    [Fact]
    public void CreateZar_InvalidIso_ThrowsEndOfStreamException()
    {
        var outDir = CreateTempDir();
        var bad = Path.Combine(outDir, "bad.iso");
        File.WriteAllBytes(bad, new byte[100]);
        var zar = Path.Combine(outDir, "bad.zar");

        Assert.Throws<EndOfStreamException>(() => XisoZarchive.CreateZar(bad, zar, 0, true));
    }

    [Fact]
    public void CreateZar_Cancellation_ThrowsOperationCanceledException()
    {
        var src = CreateSourceDir(PopulateSimple);
        var iso = CreateIso(src);
        var outDir = CreateTempDir();
        var zar = Path.Combine(outDir, "cancel.zar");
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.Throws<OperationCanceledException>(() => XisoZarchive.CreateZar(iso, zar, 0, true, cts.Token));
    }

    [Fact]
    public void CreateZar_Cancellation_FileStreamOverload_ThrowsOperationCanceledException()
    {
        var src = CreateSourceDir(PopulateSimple);
        var iso = CreateIso(src);
        var outDir = CreateTempDir();
        var zar = Path.Combine(outDir, "cancel2.zar");
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        using var fs = new FileStream(iso, FileMode.Open, FileAccess.Read, FileShare.Read, 65536);

        Assert.Throws<OperationCanceledException>(() => XisoZarchive.CreateZar(fs, 0, zar, false, true, cts.Token));
    }

    [Fact]
    public void CreateZar_LargerContent_CreatesNonEmptyZar()
    {
        var src = CreateSourceDir(d =>
        {
            File.WriteAllText(Path.Combine(d, "a.txt"), new string('a', 10000));
            File.WriteAllText(Path.Combine(d, "b.txt"), new string('b', 70000)); // > one block (64KB)
            Directory.CreateDirectory(Path.Combine(d, "sub"));
            File.WriteAllBytes(Path.Combine(d, "sub", "big.bin"), new byte[150000]);
        });
        var iso = CreateIso(src);
        var outDir = CreateTempDir();
        var zar = Path.Combine(outDir, "large.zar");

        var ok = XisoZarchive.CreateZar(iso, zar, 0, quiet: true);

        Assert.True(ok);
        var len = new FileInfo(zar).Length;
        // ZAR should be larger than just footer (144); blocks are zstd-compressed
        // (see XisoZarConvertTests for ratio assertions), so only the footer bound holds.
        Assert.True(len > 144);
        // Output must open in the real reader with all three files present.
        using var reader = ZARSharp.ZArchiveReader.TryOpen(zar);
        Assert.NotNull(reader);
        Assert.Equal(10000UL, reader.GetFileSize(reader.LookUp("a.txt")));
        Assert.Equal(70000UL, reader.GetFileSize(reader.LookUp("b.txt")));
        Assert.Equal(150000UL, reader.GetFileSize(reader.LookUp("sub/big.bin")));
    }
}