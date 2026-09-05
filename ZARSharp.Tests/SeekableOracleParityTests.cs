using System.Diagnostics;
using ZARSharp.Seekable;
using ZARSharp.Zstd;

namespace ZARSharp.Tests;

/// <summary>
/// PortPlan Step 4 acceptance: byte-parity vs the seekable-zstd/zeekstd
/// oracles both ways. The oracle is <c>seekoracle</c> (built from committed C
/// source over the frozen libzstd 1.5.7 streaming API with zeekstd's exact
/// framing policy), so "oracle bytes" are true libzstd bytes. Skipped
/// (vacuous pass) when gcc is absent; fails on any byte difference when
/// present.
/// </summary>
public sealed class SeekableOracleParityTests
{
    private static byte[] MakeInput(string kind, int n, int seed)
    {
        var kindIndex = kind switch
        {
            "zeros" => 0,
            "random" => 1,
            "text" => 2,
            "code" => 3,
            "binary" => 4,
            "hetero" => 5,
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
        var rng = new Random(unchecked((int)(0x5332026u + (uint)kindIndex * 0x9E3779B9u + (uint)n * 31u + (uint)seed * 131u)));
        var buf = new byte[n];
        switch (kind)
        {
            case "zeros":
                break;
            case "random":
                rng.NextBytes(buf);
                break;
            case "text":
            {
                const string sample = "The quick brown fox jumps over the lazy dog. Seekable parity vector. ";
                var ascii = System.Text.Encoding.ASCII.GetBytes(sample);
                for (var i = 0; i < n; i++)
                {
                    buf[i] = ascii[(i + seed) % ascii.Length];
                }

                break;
            }

            case "code":
            {
                var tokens = new[]
                {
                    "if (x == 1) { return foo(bar); }", "for (int i = 0; i < n; i++) ",
                    "    Console.WriteLine(i);", "/* comment */", "var y = x * 2 + 1;",
                };
                var ms = new MemoryStream();
                while (ms.Length < n)
                {
                    var t = System.Text.Encoding.ASCII.GetBytes(tokens[rng.Next(tokens.Length)]);
                    ms.Write(t, 0, t.Length);
                }

                buf = ms.ToArray();
                Array.Resize(ref buf, n);
                break;
            }

            case "binary":
                for (var i = 0; i < n; i++)
                {
                    buf[i] = rng.Next(100) < 70 ? (byte)rng.Next(4) : (byte)rng.Next(256);
                }

                break;
            case "hetero":
            {
                var code = MakeInput("code", n, seed);
                var binary = MakeInput("binary", n, seed);
                Array.Copy(code, buf, n);
                var secondStart = 131072 + 65536;
                var secondEnd = Math.Min(n, 131072 + 131072);
                if (secondEnd > secondStart)
                {
                    Array.Copy(binary, secondStart, buf, secondStart, secondEnd - secondStart);
                }

                break;
            }

            default:
                throw new ArgumentOutOfRangeException(nameof(kind));
        }

        return buf;
    }

    private static string TempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "zarsharp-seekable", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void RunOracle(string exe, params string[] args)
    {
        using var proc = Process.Start(new ProcessStartInfo
        {
            FileName = exe,
            Arguments = string.Join(" ", args.Select(a => $"\"{a}\"")),
            RedirectStandardError = true,
            UseShellExecute = false,
        });
        Assert.NotNull(proc);
        proc.WaitForExit(300000);
        if (proc.ExitCode != 0)
        {
            Assert.Fail("seekoracle failed: " + proc.StandardError.ReadToEnd());
        }
    }

    private static byte[] OursFoot(byte[] input, int level, int frameSize, bool checksum)
    {
        var writer = new SeekableWriter(new SeekableOptions { Level = level, FrameSize = frameSize, Checksum = checksum });
        writer.Write(input);
        return writer.Finish();
    }

    public static IEnumerable<object[]> FootCases()
    {
        string[] kinds = ["zeros", "random", "text", "code", "binary", "hetero"];
        int[] sizes = [0, 1, 1000, 70000, 131072, 131073, 200000, 300000];
        int[] levels = [1, 3, 6, 13, 19];
        int[] frameSizes = [4096, 65536, 100000];
        foreach (var level in levels)
        {
            foreach (var size in sizes)
            {
                foreach (var kind in kinds)
                {
                    yield return [kind, size, level, frameSizes[(size + level) % frameSizes.Length], true];
                }
            }
        }

        // Checksum off, default (single-frame) and odd frame sizes.
        yield return ["text", 200000, 3, 2 * 1024 * 1024, false];
        yield return ["binary", 300000, 6, 70000, false];
        yield return ["code", 131073, 13, 2 * 1024 * 1024, false];
        yield return ["hetero", 300000, 16, 100000, true];
        yield return ["binary", 200000, 19, 4096, true];

        // Frames whose content is an exact multiple of 128 KiB end with an
        // empty last block (streaming e_end on an empty inBuff buffer).
        yield return ["binary", 300000, 3, 131072, true];
        yield return ["text", 262144, 6, 131072, false];
        yield return ["code", 262144, 13, 131072, true];
        yield return ["random", 131072, 1, 131072, true];
    }

    [Theory]
    [MemberData(nameof(FootCases))]
    public void Foot_ByteIdenticalToOracle(string kind, int size, int level, int frameSize, bool checksum)
    {
        var exe = SeekOracle.ExePath;
        if (exe is null)
        {
            return;
        }

        var dir = TempDir();
        try
        {
            var input = MakeInput(kind, size, 17);
            File.WriteAllBytes(Path.Combine(dir, "in.bin"), input);
            var args = new List<string>
            {
                "enc", Path.Combine(dir, "in.bin"), Path.Combine(dir, "oracle.zst"),
                "--level", level.ToString(System.Globalization.CultureInfo.InvariantCulture),
                "--usize", frameSize.ToString(System.Globalization.CultureInfo.InvariantCulture),
                checksum ? "--checksum" : "--no-checksum",
            };
            RunOracle(exe, [.. args]);
            var expected = File.ReadAllBytes(Path.Combine(dir, "oracle.zst"));
            Assert.Equal(expected, OursFoot(input, level, frameSize, checksum));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Theory]
    [InlineData("text", 200000, 3, 50000, true)]
    [InlineData("binary", 131073, 6, 70000, false)]
    [InlineData("code", 300000, 13, 100000, true)]
    [InlineData("zeros", 1000, 1, 100, true)]
    public void Head_ByteIdenticalToOracle(string kind, int size, int level, int frameSize, bool checksum)
    {
        var exe = SeekOracle.ExePath;
        if (exe is null)
        {
            return;
        }

        var dir = TempDir();
        try
        {
            var input = MakeInput(kind, size, 23);
            File.WriteAllBytes(Path.Combine(dir, "in.bin"), input);
            RunOracle(exe, "enc", Path.Combine(dir, "in.bin"), Path.Combine(dir, "oracle.zst"),
                "--level", level.ToString(System.Globalization.CultureInfo.InvariantCulture),
                "--usize", frameSize.ToString(System.Globalization.CultureInfo.InvariantCulture),
                checksum ? "--checksum" : "--no-checksum",
                "--head", Path.Combine(dir, "oracle.tbl"));
            var expectedData = File.ReadAllBytes(Path.Combine(dir, "oracle.zst"));
            var expectedTable = File.ReadAllBytes(Path.Combine(dir, "oracle.tbl"));

            var writer = new SeekableWriter(new SeekableOptions { Level = level, FrameSize = frameSize, Checksum = checksum });
            writer.Write(input);
            var (data, table) = writer.FinishHead();
            Assert.Equal(expectedData, data);
            Assert.Equal(expectedTable, table);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Theory]
    [InlineData("text", 200000, 3, 8192, true)]
    [InlineData("binary", 200000, 3, 16384, false)]
    [InlineData("code", 131073, 6, 4096, true)]
    [InlineData("random", 150000, 1, 32768, true)]
    public void CompressedPolicy_ByteIdenticalToOracle(string kind, int size, int level, int frameSize, bool checksum)
    {
        var exe = SeekOracle.ExePath;
        if (exe is null)
        {
            return;
        }

        var dir = TempDir();
        try
        {
            var input = MakeInput(kind, size, 29);
            File.WriteAllBytes(Path.Combine(dir, "in.bin"), input);
            RunOracle(exe, "enc", Path.Combine(dir, "in.bin"), Path.Combine(dir, "oracle.zst"),
                "--level", level.ToString(System.Globalization.CultureInfo.InvariantCulture),
                "--csize", frameSize.ToString(System.Globalization.CultureInfo.InvariantCulture),
                checksum ? "--checksum" : "--no-checksum");
            var expected = File.ReadAllBytes(Path.Combine(dir, "oracle.zst"));

            var writer = new SeekableWriter(new SeekableOptions
            {
                Level = level, FrameSize = frameSize,
                Policy = SeekableFrameSizePolicy.Compressed, Checksum = checksum,
            });
            writer.Write(input);
            Assert.Equal(expected, writer.Finish());
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Theory]
    [InlineData("binary", 200000, 3, 50000, true)]
    [InlineData("code", 131073, 13, 70000, false)]
    [InlineData("text", 300000, 19, 100000, true)]
    public void DecodeInterop_BothWays(string kind, int size, int level, int frameSize, bool checksum)
    {
        var exe = SeekOracle.ExePath;
        if (exe is null)
        {
            return;
        }

        var dir = TempDir();
        try
        {
            var input = MakeInput(kind, size, 31);
            File.WriteAllBytes(Path.Combine(dir, "in.bin"), input);
            RunOracle(exe, "enc", Path.Combine(dir, "in.bin"), Path.Combine(dir, "oracle.zst"),
                "--level", level.ToString(System.Globalization.CultureInfo.InvariantCulture),
                "--usize", frameSize.ToString(System.Globalization.CultureInfo.InvariantCulture),
                checksum ? "--checksum" : "--no-checksum");
            var oracleFile = File.ReadAllBytes(Path.Combine(dir, "oracle.zst"));
            var ourFile = OursFoot(input, level, frameSize, checksum);

            // Our reader decodes the oracle file (ranges included).
            var reader = new SeekableReader(oracleFile);
            Assert.Equal(input, reader.DecompressAll());
            var rng = new Random(0x5332026);
            for (var i = 0; i < 10; i++)
            {
                var start = rng.Next(size);
                var len = rng.Next(size - start + 1);
                Assert.Equal(input.AsSpan(start, len).ToArray(), reader.DecompressRange(start, len));
            }

            // The oracle decoder reads our file (full + subrange).
            File.WriteAllBytes(Path.Combine(dir, "ours.zst"), ourFile);
            RunOracle(exe, "dec", Path.Combine(dir, "ours.zst"), Path.Combine(dir, "back.bin"));
            Assert.Equal(input, File.ReadAllBytes(Path.Combine(dir, "back.bin")));
            RunOracle(exe, "dec", Path.Combine(dir, "ours.zst"), Path.Combine(dir, "part.bin"),
                "--from", (size / 4).ToString(System.Globalization.CultureInfo.InvariantCulture),
                "--to", (size / 4 + size / 2).ToString(System.Globalization.CultureInfo.InvariantCulture));
            Assert.Equal(
                input.AsSpan(size / 4, size / 2).ToArray(),
                File.ReadAllBytes(Path.Combine(dir, "part.bin")));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>
    /// Independent validity: stock zstd decodes our seekable files
    /// (concatenated frames, trailing skippable table, empty-block tails,
    /// checksums) with no seekable awareness.
    /// </summary>
    [Theory]
    [InlineData("text", 200000, 3, 50000, true)]
    [InlineData("binary", 300000, 19, 131072, true)]
    [InlineData("code", 131073, 13, 70000, false)]
    public void StockZstd_DecodesOurFiles(string kind, int size, int level, int frameSize, bool checksum)
    {
        var python = FindPythonWithZstd();
        if (python is null)
        {
            return;
        }

        var dir = TempDir();
        try
        {
            var input = MakeInput(kind, size, 37);
            var file = OursFoot(input, level, frameSize, checksum);
            File.WriteAllBytes(Path.Combine(dir, "in.bin"), input);
            File.WriteAllBytes(Path.Combine(dir, "ours.zst"), file);
            using var proc = Process.Start(new ProcessStartInfo(python,
                "-c \"import compression.zstd as z,sys; "
                + "a=open(sys.argv[1],'rb').read(); b=open(sys.argv[2],'rb').read(); "
                + "sys.exit(0 if z.decompress(a)==b else 1)\" "
                + $"\"{Path.Combine(dir, "ours.zst")}\" \"{Path.Combine(dir, "in.bin")}\"")
            {
                UseShellExecute = false,
            });
            Assert.NotNull(proc);
            proc.WaitForExit(300000);
            Assert.Equal(0, proc.ExitCode);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    private static string? FindPythonWithZstd()
    {
        string[] candidates = OperatingSystem.IsWindows() ? ["py", "python", "python3"] : ["python3", "python"];
        foreach (var candidate in candidates)
        {
            try
            {
                using var probe = Process.Start(new ProcessStartInfo(candidate, "-c \"import compression.zstd\"")
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                });
                if (probe is null)
                {
                    continue;
                }

                probe.WaitForExit(15_000);
                if (probe.ExitCode == 0)
                {
                    return candidate;
                }
            }
            catch
            {
                // try next
            }
        }

        return null;
    }

    [Fact]
    public void OracleConstants_MatchModel()
    {
        var exe = SeekOracle.ExePath;
        if (exe is null)
        {
            return;
        }

        using var proc = Process.Start(new ProcessStartInfo
        {
            FileName = exe,
            Arguments = "info",
            RedirectStandardOutput = true,
            UseShellExecute = false,
        });
        Assert.NotNull(proc);
        var output = proc.StandardOutput.ReadToEnd();
        proc.WaitForExit(60000);
        Assert.Contains("in=131072", output, StringComparison.Ordinal);
        // C# model: CLI read size + streaming output buffer size.
        Assert.Equal(131072, SeekableWriter.InputChunkSize);
        Assert.Contains("out=131591", output, StringComparison.Ordinal);
        Assert.Equal(131591, ZstdCompressor.GetCompressBound(131072) + 3 + 4);
    }
}
