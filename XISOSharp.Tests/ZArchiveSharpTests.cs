using System.Diagnostics;
using System.Security.Cryptography;
using ZARSharp;
using ZARSharp.Zstd;

namespace XISOSharp.Tests;

/// <summary>
/// Tests for the ZARSharp pure-C# ZArchive port: writer/reader round-trips,
/// format vectors, the zstd decoder, and interop with the reference
/// <c>zarchive.exe</c> in both directions.
/// </summary>
public sealed class ZArchiveSharpTests
{
    private static string SolutionRoot()
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

        throw new InvalidOperationException("Solution root not found.");
    }

    private static string ZArchiveExePath() =>
        Path.Combine(SolutionRoot(), "References", "ZArchive-0.1.2", "zarchive.exe");

    private static string NewTempDir(string prefix)
    {
        string dir = Path.Combine(Path.GetTempPath(), "zarsharp", prefix + "_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static byte[] PatternBytes(int length, int seed = 0)
    {
        byte[] data = new byte[length];
        uint state = (uint)(seed * 2654435761u + 1);
        for (int i = 0; i < length; i++)
        {
            state = state * 1664525 + 1013904223;
            data[i] = (byte)(state >> 24);
        }

        return data;
    }

    private static byte[] BuildArchive(Action<ZArchiveWriter> build)
    {
        using var ms = new MemoryStream();
        using (var writer = new ZArchiveWriter(ms))
        {
            build(writer);
            writer.Finalize();
        }

        return ms.ToArray();
    }

    // ------------------------------------------------------------------
    // Round-trips (writer raw blocks -> reader)
    // ------------------------------------------------------------------

    [Fact]
    public void RoundTrip_SingleSmallFile()
    {
        byte[] content = System.Text.Encoding.ASCII.GetBytes("Hello, ZArchive!");
        byte[] zar = BuildArchive(w =>
        {
            Assert.True(w.StartNewFile("hello.txt"));
            w.AppendData(content);
        });

        using var reader = ZArchiveReader.TryOpen(zar);
        Assert.NotNull(reader);
        uint node = reader!.LookUp("hello.txt");
        Assert.NotEqual(ZArchiveReader.InvalidNode, node);
        Assert.True(reader.IsFile(node));
        Assert.Equal((ulong)content.Length, reader.GetFileSize(node));
        Assert.Equal(content, reader.ReadFile(node));
    }

    [Fact]
    public void RoundTrip_SizesAroundBlockBoundary()
    {
        int[] sizes = [0, 1, 100, 65535, 65536, 65537, 131072, 200000];
        var contents = sizes.Select((s, i) => (Name: $"f{i}.bin", Data: PatternBytes(s, i))).ToList();
        byte[] zar = BuildArchive(w =>
        {
            foreach (var (name, data) in contents)
            {
                Assert.True(w.StartNewFile(name));
                // Split appends to exercise the staging buffer.
                for (int off = 0; off < data.Length; off += 7777)
                {
                    w.AppendData(data.AsSpan(off, Math.Min(7777, data.Length - off)));
                }
            }
        });

        using var reader = ZArchiveReader.TryOpen(zar);
        Assert.NotNull(reader);
        foreach (var (name, data) in contents)
        {
            uint node = reader!.LookUp(name);
            Assert.NotEqual(ZArchiveReader.InvalidNode, node);
            Assert.Equal((ulong)data.Length, reader.GetFileSize(node));
            Assert.Equal(data, reader.ReadFile(node));
        }
    }

    [Fact]
    public void RoundTrip_NestedDirsAndCaseInsensitiveLookup()
    {
        byte[] zar = BuildArchive(w =>
        {
            Assert.True(w.MakeDir("Docs"));
            Assert.True(w.MakeDir("Docs/Sub", recursive: true));
            Assert.True(w.StartNewFile("Docs/ReadMe.TXT"));
            w.AppendData([1, 2, 3]);
            Assert.True(w.StartNewFile("docs/sub/data.bin"));
            w.AppendData([4, 5]);
        });

        using var reader = ZArchiveReader.TryOpen(zar);
        Assert.NotNull(reader);
        // Case-insensitive, both separators.
        Assert.NotEqual(ZArchiveReader.InvalidNode, reader!.LookUp("DOCS\\readme.txt"));
        Assert.NotEqual(ZArchiveReader.InvalidNode, reader.LookUp("/docs/SUB/DATA.BIN"));
        uint dir = reader.LookUp("docs/sub");
        Assert.True(reader.IsDirectory(dir));
        Assert.Equal(1u, reader.GetDirEntryCount(dir));
        Assert.True(reader.GetDirEntry(dir, 0, out var entry));
        Assert.Equal("data.bin", entry.Name);
        Assert.True(entry.IsFile);
        Assert.Equal(2ul, entry.Size);
    }

    [Fact]
    public void Writer_ApiFailures()
    {
        using var ms = new MemoryStream();
        using var w = new ZArchiveWriter(ms);
        Assert.False(w.StartNewFile("no/such/dir/f.txt")); // missing parent
        Assert.True(w.MakeDir("a", recursive: true));
        Assert.False(w.MakeDir("a")); // already exists
        Assert.True(w.StartNewFile("a/f.txt"));
        Assert.False(w.StartNewFile("A/F.TXT")); // case-insensitive duplicate
        Assert.False(w.MakeDir("a/f.txt")); // file blocks dir
        w.Finalize();
    }

    [Fact]
    public void Reader_InvalidArchivesReturnNull()
    {
        Assert.Null(ZArchiveReader.TryOpen([]));
        Assert.Null(ZArchiveReader.TryOpen(new byte[144])); // size <= footer
        byte[] zeros = new byte[1024];
        Assert.Null(ZArchiveReader.TryOpen(zeros)); // bad magic
        Assert.Null(ZArchiveReader.TryOpen((string)"nonexistent_xyz.zar"));
    }

    [Fact]
    public void Reader_ReadClampsAndCrossesBlocks()
    {
        byte[] data = PatternBytes(200000, 42);
        byte[] zar = BuildArchive(w =>
        {
            Assert.True(w.StartNewFile("big.bin"));
            w.AppendData(data);
        });

        using var reader = ZArchiveReader.TryOpen(zar)!;
        uint node = reader!.LookUp("big.bin");
        byte[] span = new byte[70000];
        // Unaligned read crossing a block boundary.
        ulong read = reader.ReadFromFile(node, 60000, span);
        Assert.Equal(70000ul, read);
        Assert.Equal(data.AsSpan(60000, 70000).ToArray(), span);
        // Clamp at EOF.
        byte[] tail = new byte[100];
        Assert.Equal(5ul, reader.ReadFromFile(node, (ulong)data.Length - 5, tail));
        Assert.Equal(data.AsSpan(data.Length - 5).ToArray(), tail.AsSpan(0, 5).ToArray());
        // Offset past EOF.
        Assert.Equal(0ul, reader.ReadFromFile(node, (ulong)data.Length, tail));
    }

    // ------------------------------------------------------------------
    // Format vectors
    // ------------------------------------------------------------------

    [Fact]
    public void Format_FooterIsBigEndianWithMagicAtEnd()
    {
        byte[] zar = BuildArchive(w =>
        {
            Assert.True(w.StartNewFile("a"));
            w.AppendData([9]);
        });

        Assert.True(zar.Length > 144);
        byte[] footer = zar[^144..];
        // totalSize (BE u64 at offset 128) == file size.
        ulong total = ((ulong)footer[128] << 56) | ((ulong)footer[129] << 48) |
                      ((ulong)footer[130] << 40) | ((ulong)footer[131] << 32) |
                      ((ulong)footer[132] << 24) | ((ulong)footer[133] << 16) |
                      ((ulong)footer[134] << 8) | footer[135];
        Assert.Equal((ulong)zar.Length, total);
        // version then magic at the very end.
        Assert.Equal(new byte[] { 0x61, 0xBF, 0x3A, 0x01 }, footer[136..140]);
        Assert.Equal(new byte[] { 0x16, 0x9F, 0x52, 0xD6 }, footer[140..144]);
    }

    [Fact]
    public void Format_IntegrityHashCoversOutput()
    {
        byte[] zar = BuildArchive(w =>
        {
            Assert.True(w.StartNewFile("a"));
            w.AppendData(PatternBytes(1000, 7));
        });

        byte[] footer = zar[^144..];
        byte[] stored = footer[96..128];
        // Recompute: SHA-256 over everything before the footer + zeroed footer.
        byte[] zeroed = (byte[])footer.Clone();
        Array.Clear(zeroed, 96, 32);
        using var sha = SHA256.Create();
        sha.TransformBlock(zar, 0, zar.Length - 144, null, 0);
        sha.TransformFinalBlock(zeroed, 0, zeroed.Length);
        Assert.Equal(sha.Hash!, stored);
    }

    [Fact]
    public void Format_LongNameQuirk()
    {
        // Writer truncates names at 0x7FFF chars.
        string longName = new string('n', 0x8005) + ".txt";
        byte[] zar = BuildArchive(w =>
        {
            Assert.True(w.StartNewFile(longName));
            w.AppendData([1]);
        });

        using var reader = ZArchiveReader.TryOpen(zar)!;
        // The stored (>= 0x80 char) name hits the 0.1.2 extended-length reader
        // quirk and is not resolvable -- byte parity with the C++ reader.
        Assert.Equal(ZArchiveReader.InvalidNode, reader!.LookUp(longName));
    }

    [Fact]
    public void Common_PathHelpersMatchCpp()
    {
        ReadOnlySpan<char> p = "//a\\b/c".AsSpan();
        Assert.True(ZArchiveCommon.GetNextPathNode(ref p, out var n1));
        Assert.Equal("a", n1.ToString());
        Assert.True(ZArchiveCommon.GetNextPathNode(ref p, out var n2));
        Assert.Equal("b", n2.ToString());
        Assert.True(ZArchiveCommon.GetNextPathNode(ref p, out var n3));
        Assert.Equal("c", n3.ToString());
        Assert.False(ZArchiveCommon.GetNextPathNode(ref p, out _));

        ReadOnlySpan<char> dir = "a/b/file.txt".AsSpan();
        ZArchiveCommon.SplitFilenameFromPath(ref dir, out var file);
        Assert.Equal("file.txt", file.ToString());
        Assert.Equal("a/b/", dir.ToString());

        // Reversed-sign comparator, ascending sort via "> 0" predicate.
        Assert.True(ZArchiveCommon.CompareNodeName("a".AsSpan(), "b".AsSpan()) > 0);
        Assert.Equal(0, ZArchiveCommon.CompareNodeName("AbC".AsSpan(), "aBc".AsSpan()));
        Assert.True(ZArchiveCommon.CompareNodeNameBool("AbC".AsSpan(), "aBc".AsSpan()));
        Assert.Equal(1, ZArchiveCommon.CompareNodeName("ab".AsSpan(), "abc".AsSpan()));
    }

    // ------------------------------------------------------------------
    // zstd decoder units (hand-built frames)
    // ------------------------------------------------------------------

    private static byte[] RawBlockFrame(byte[] content)
    {
        // Single-segment frame with FCS + one raw block.
        var frame = new List<byte> { 0x28, 0xB5, 0x2F, 0xFD };
        if (content.Length < 256)
        {
            frame.Add(0x20); // FCS flag 0, single-segment
            frame.Add((byte)content.Length);
        }
        else
        {
            frame.Add(0x60); // FCS flag 1, single-segment
            int v = content.Length - 256;
            Assert.True(v <= 0xFFFF);
            frame.Add((byte)(v & 0xFF));
            frame.Add((byte)(v >> 8));
        }

        uint header = (uint)((content.Length << 3) | (0 << 1) | 1);
        frame.Add((byte)(header & 0xFF));
        frame.Add((byte)((header >> 8) & 0xFF));
        frame.Add((byte)((header >> 16) & 0xFF));
        frame.AddRange(content);
        return [.. frame];
    }

    [Fact]
    public void Zstd_RawBlockFrame()
    {
        byte[] content = PatternBytes(1000, 3);
        byte[] decoded = ZstdDecompressor.Decompress(RawBlockFrame(content));
        Assert.Equal(content, decoded);
    }

    [Fact]
    public void Zstd_RleBlockAndSkippableAndConcat()
    {
        // RLE block frame: "A" x 500. Single-segment FCS=500 -> 2-byte form (500-256).
        var frame = new List<byte> { 0x28, 0xB5, 0x2F, 0xFD, 0x60, 0xF4, 0x00 };
        uint header = (uint)((500 << 3) | (1 << 1) | 1);
        frame.Add((byte)(header & 0xFF));
        frame.Add((byte)((header >> 8) & 0xFF));
        frame.Add((byte)((header >> 16) & 0xFF));
        frame.Add(0x41);
        byte[] decoded = ZstdDecompressor.Decompress([.. frame]);
        Assert.Equal(500, decoded.Length);
        Assert.All(decoded, b => Assert.Equal(0x41, b));

        // Skippable frame + concat of two tiny frames.
        var blob = new List<byte> { 0x50, 0x2A, 0x4D, 0x18, 0x03, 0x00, 0x00, 0x00, 0xDE, 0xAD, 0xBE };
        blob.AddRange(RawBlockFrame([7, 8, 9]));
        blob.AddRange(RawBlockFrame([10]));
        Assert.Equal([7, 8, 9, 10], ZstdDecompressor.Decompress([.. blob]));
    }

    [Fact]
    public void Zstd_RejectsGarbage()
    {
        Assert.Throws<ZstdException>(() => ZstdDecompressor.Decompress([0x01, 0x02, 0x03, 0x04]));
        Assert.Throws<ZstdException>(() => ZstdDecompressor.Decompress(
            [0x28, 0xB5, 0x2F, 0xFD, 0x60, 0x00, 0xFF, 0xFF, 0xFF, 0xFF]));
    }

    [Fact]
    public void Zstd_ChecksumFrameRoundTrip()
    {
        // Frame with content checksum over "abc": verified against the
        // decoder (checksum mismatch must throw; covered below).
        byte[] content = [0x61, 0x62, 0x63];
        var frame = new List<byte> { 0x28, 0xB5, 0x2F, 0xFD, 0x24, 0x03 };
        uint header = (uint)((content.Length << 3) | (0 << 1) | 1);
        frame.Add((byte)(header & 0xFF));
        frame.Add((byte)((header >> 8) & 0xFF));
        frame.Add((byte)((header >> 16) & 0xFF));
        frame.AddRange(content);
        // XXH64("abc", 0) = 0x44BC2CF5AD770999; low 32 bits LE.
        frame.AddRange([0x99, 0x09, 0x77, 0xAD]);
        Assert.Equal(content, ZstdDecompressor.Decompress([.. frame]));

        // Corrupt the checksum -> must throw.
        byte[] bad = [.. frame];
        bad[^1] ^= 0xFF;
        Assert.Throws<ZstdException>(() => ZstdDecompressor.Decompress(bad));
    }

    // ------------------------------------------------------------------
    // Interop with the reference zarchive.exe
    // ------------------------------------------------------------------

    private static void RunExe(string input, string output)
    {
        string exe = ZArchiveExePath();
        var psi = new ProcessStartInfo(exe, $"\"{input}\" \"{output}\"")
        {
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var proc = Process.Start(psi)!;
        proc.WaitForExit(120000);
        Assert.True(proc.ExitCode == 0, $"zarchive.exe failed: {proc.StandardOutput.ReadToEnd()}");
    }

    [Fact]
    public void Interop_ReadExePackedArchive()
    {
        if (!File.Exists(ZArchiveExePath()))
        {
            return; // reference binary not present; covered by round-trip tests
        }

        string tmp = NewTempDir("exe2sharp");
        try
        {
            string indir = Path.Combine(tmp, "in");
            Directory.CreateDirectory(indir);
            var files = new Dictionary<string, byte[]>(StringComparer.Ordinal)
            {
                ["hello.txt"] = System.Text.Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat("Hello ZArchive world! ", 5000))),
                ["blob.bin"] = PatternBytes(65536, 11),
                ["zeros.bin"] = new byte[70000],
                ["empty.bin"] = [],
                [Path.Combine("sub", "deep.bin")] = PatternBytes(100000, 99),
            };
            foreach (var (rel, data) in files)
            {
                string full = Path.Combine(indir, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                File.WriteAllBytes(full, data);
            }

            // Note: the reference packs empty dirs only via MakeDir for real dirs;
            // empty files ARE packed (0-size, no blocks). Verify each file.
            string zar = Path.Combine(tmp, "ref.zar");
            RunExe(indir, zar);

            using var reader = ZArchiveReader.TryOpen(zar);
            Assert.NotNull(reader);
            foreach (var (rel, data) in files)
            {
                string zpath = rel.Replace('\\', '/');
                if (data.Length == 0)
                {
                    // Empty files still get directory entries.
                    uint n = reader!.LookUp(zpath);
                    Assert.NotEqual(ZArchiveReader.InvalidNode, n);
                    Assert.Equal(0ul, reader.GetFileSize(n));
                    continue;
                }

                uint node = reader!.LookUp(zpath);
                Assert.NotEqual(ZArchiveReader.InvalidNode, node);
                Assert.Equal(data, reader.ReadFile(node));
            }
        }
        finally
        {
            try { Directory.Delete(tmp, true); } catch { }
        }
    }

    [Fact]
    public void Interop_ExeReadsOurArchive()
    {
        if (!File.Exists(ZArchiveExePath()))
        {
            return;
        }

        string tmp = NewTempDir("sharp2exe");
        try
        {
            string indir = Path.Combine(tmp, "in");
            Directory.CreateDirectory(indir);
            var files = new Dictionary<string, byte[]>(StringComparer.Ordinal)
            {
                ["a.txt"] = System.Text.Encoding.ASCII.GetBytes("sharp packs, exe unpacks"),
                ["big.bin"] = PatternBytes(200000, 5),
                [Path.Combine("d1", "d2", "deep.txt")] = System.Text.Encoding.ASCII.GetBytes("deep"),
            };
            foreach (var (rel, data) in files)
            {
                string full = Path.Combine(indir, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                File.WriteAllBytes(full, data);
            }

            string zar = Path.Combine(tmp, "ours.zar");
            ZArchiveTool.Pack(indir, zar);
            string outdir = Path.Combine(tmp, "out");
            RunExe(zar, outdir);

            foreach (var (rel, data) in files)
            {
                Assert.Equal(data, File.ReadAllBytes(Path.Combine(outdir, rel)));
            }
        }
        finally
        {
            try { Directory.Delete(tmp, true); } catch { }
        }
    }

    // ------------------------------------------------------------------
    // Stress: multi-record offsets, LRU eviction, concurrency, edge cases
    // ------------------------------------------------------------------

    [Fact]
    public void Stress_MultiRecordAndLruEviction()
    {
        // 5 MiB file = 80 blocks > 64-block cache + > 16 blocks/record.
        byte[] data = PatternBytes(5 * 1024 * 1024, 1234);
        byte[] zar = BuildArchive(w =>
        {
            Assert.True(w.StartNewFile("big.bin"));
            w.AppendData(data);
        });

        using var reader = ZArchiveReader.TryOpen(zar)!;
        uint node = reader!.LookUp("big.bin");
        Assert.NotEqual(ZArchiveReader.InvalidNode, node);
        // Read tail first (fills cache with late blocks), then head (evicted reload).
        byte[] tail = new byte[100000];
        Assert.Equal(100000ul, reader.ReadFromFile(node, (ulong)data.Length - 100000, tail));
        Assert.Equal(data.AsSpan(data.Length - 100000).ToArray(), tail);
        Assert.Equal(data, reader.ReadFile(node));
        // Strided reads across every block.
        byte[] one = new byte[1];
        for (int b = 0; b < 80; b++)
        {
            Assert.Equal(1ul, reader.ReadFromFile(node, (ulong)(b * 65536 + b), one));
            Assert.Equal(data[b * 65536 + b], one[0]);
        }
    }

    [Fact]
    public async Task Stress_ConcurrentReads()
    {
        byte[] data = PatternBytes(300000, 77);
        byte[] zar = BuildArchive(w =>
        {
            Assert.True(w.StartNewFile("c.bin"));
            w.AppendData(data);
        });

        using var reader = ZArchiveReader.TryOpen(zar)!;
        uint node = reader!.LookUp("c.bin");
        var tasks = Enumerable.Range(0, 8).Select(t => Task.Run(() =>
        {
            var rnd = new Random(t);
            byte[] buf = new byte[5000];
            for (int i = 0; i < 25; i++)
            {
                int off = rnd.Next(0, data.Length - 5000);
                Assert.Equal(5000ul, reader.ReadFromFile(node, (ulong)off, buf));
                Assert.Equal(data.AsSpan(off, 5000).ToArray(), buf);
            }
        })).ToArray();
        await Task.WhenAll(tasks);
    }

    [Fact]
    public void Edge_LookupAndDirEntrySemantics()
    {
        byte[] zar = BuildArchive(w =>
        {
            Assert.True(w.MakeDir("empty"));
            Assert.True(w.StartNewFile("empty/")); // empty filename entry (C++ allows it)
            Assert.True(w.StartNewFile("top.txt"));
            w.AppendData([1]);
        });

        using var reader = ZArchiveReader.TryOpen(zar)!;
        Assert.Equal(0u, reader!.LookUp(string.Empty)); // root
        Assert.Equal(0u, reader.LookUp("/"));
        Assert.Equal(0u, reader.LookUp("///"));
        Assert.Equal(ZArchiveReader.InvalidNode, reader.LookUp("missing"));
        Assert.Equal(ZArchiveReader.InvalidNode, reader.LookUp("top.txt/deeper")); // iterate a file
        uint top = reader.LookUp("top.txt");
        Assert.Equal(0u, reader.GetDirEntryCount(top)); // file has no children
        Assert.False(reader.GetDirEntry(top, 0, out _));
        Assert.False(reader.GetDirEntry(0, 9999, out _));
        Assert.False(reader.IsFile(9999));
        Assert.False(reader.IsDirectory(9999));
        Assert.Equal(0ul, reader.GetFileSize(9999));
    }

    [Fact]
    public void Edge_CorruptedArchivesReturnNull()
    {
        byte[] zar = BuildArchive(w =>
        {
            Assert.True(w.StartNewFile("a"));
            w.AppendData(PatternBytes(100, 1));
        });

        byte[] badMagic = (byte[])zar.Clone();
        badMagic[^1] ^= 0xFF;
        Assert.Null(ZArchiveReader.TryOpen(badMagic));

        byte[] badTotal = (byte[])zar.Clone();
        badTotal[badTotal.Length - 144 + 130] ^= 0xFF; // totalSize byte
        Assert.Null(ZArchiveReader.TryOpen(badTotal));

        byte[] truncated = zar[..^10];
        Assert.Null(ZArchiveReader.TryOpen(truncated));
    }

    [Fact]
    public void Interop_RandomIncompressibleBothWays()
    {
        if (!File.Exists(ZArchiveExePath()))
        {
            return;
        }

        string tmp = NewTempDir("incompress");
        try
        {
            string indir = Path.Combine(tmp, "in");
            Directory.CreateDirectory(indir);
            var rnd = new Random(20260903);
            var files = new Dictionary<string, byte[]>(StringComparer.Ordinal)
            {
                ["rand1.bin"] = new byte[70000],
                ["rand2.bin"] = new byte[200000],
            };
            foreach (var key in files.Keys.ToList())
            {
                rnd.NextBytes(files[key]);
            }

            foreach (var (rel, data) in files)
            {
                File.WriteAllBytes(Path.Combine(indir, rel), data);
            }

            // exe -> ZARSharp (incompressible => raw ZArchive blocks).
            string refZar = Path.Combine(tmp, "ref.zar");
            RunExe(indir, refZar);
            using (var reader = ZArchiveReader.TryOpen(refZar))
            {
                Assert.NotNull(reader);
                foreach (var (rel, data) in files)
                {
                    Assert.Equal(data, reader!.ReadFile(reader.LookUp(rel)));
                }
            }

            // ZARSharp -> exe.
            string ourZar = Path.Combine(tmp, "ours.zar");
            ZArchiveTool.Pack(indir, ourZar);
            string outdir = Path.Combine(tmp, "out");
            RunExe(ourZar, outdir);
            foreach (var (rel, data) in files)
            {
                Assert.Equal(data, File.ReadAllBytes(Path.Combine(outdir, rel)));
            }
        }
        finally
        {
            try { Directory.Delete(tmp, true); } catch { }
        }
    }

    [Fact]
    public void Tool_PackExtractRoundTrip()
    {
        string tmp = NewTempDir("toolrt");
        try
        {
            string indir = Path.Combine(tmp, "in");
            Directory.CreateDirectory(Path.Combine(indir, "sub"));
            File.WriteAllBytes(Path.Combine(indir, "x.txt"), [1, 2, 3]);
            File.WriteAllBytes(Path.Combine(indir, "sub", "y.bin"), PatternBytes(70000, 1));
            string zar = Path.Combine(tmp, "t.zar");
            ZArchiveTool.Pack(indir, zar);
            string outdir = Path.Combine(tmp, "out");
            ZArchiveTool.Extract(zar, outdir);
            Assert.Equal([1, 2, 3], File.ReadAllBytes(Path.Combine(outdir, "x.txt")));
            Assert.Equal(PatternBytes(70000, 1), File.ReadAllBytes(Path.Combine(outdir, "sub", "y.bin")));
            // Refuse to overwrite.
            Assert.Throws<IOException>(() => ZArchiveTool.Pack(indir, zar));
        }
        finally
        {
            try { Directory.Delete(tmp, true); } catch { }
        }
    }
}
