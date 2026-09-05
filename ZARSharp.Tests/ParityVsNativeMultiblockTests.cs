using System.Diagnostics;
using System.Text.Json;
using ZARSharp.Zstd;

namespace ZARSharp.Tests;

/// <summary>
/// PortPlan Step 3 follow-up (M1 framing + M2 persistence + M3 entropy
/// reuse + M4 splitter): multi-block parity. Single-shot native
/// <c>ZSTD_compress</c> uses 128 KiB blocks (<c>ZSTD_BLOCKSIZE_MAX</c>), one
/// parameter row from the TOTAL size shared by every block,
/// <c>blockSize - ZSTD_minGain</c> raw fallback per block, <c>bt_rle</c> for
/// tiny uniform non-first blocks, and direct raw for blocks below 7 bytes.
/// Match tables, optimal-parser statistics and entropy tables all persist
/// across blocks (<see cref="ZstdFrameState"/> + staged/confirmed
/// <see cref="ZstdEntropyState"/>); only emitted compressed blocks confirm
/// them, with the offset-code valid→check downgrade on every block.
/// Optimal-parser levels with windowLog ≥ 17 additionally run the post-block
/// splitter (recursive entropy-estimation search over the parsed sequences,
/// per-partition repcode reconciliation, staged entropy confirmed only for
/// emitted compressed partitions) and the pre-splitter (raw-byte fingerprint
/// cutting full 128 KiB input chunks past the first when savings allow).
/// This asserts byte-identity where the ported machinery covers native behavior:
/// <list type="bullet">
/// <item><c>random</c>/<c>zeros</c> at every level and multi-block size (raw
/// and RLE blocks are history-independent);</item>
/// <item><c>text</c> and <c>binary</c> at 65537..131073 (single native block,
/// or a trivial raw tail);</item>
/// <item><c>code</c> at 65537..131073 for levels 1..12 (the post-block
/// splitter is structurally disabled below btopt, so no data-dependent split
/// can occur; level 13+ needs M4 even when some draws happen to pass);</item>
/// <item>persistent-state proofs: <c>text</c> 200000/300000 at levels 1..12
/// (fast through btlazy2), <c>code</c> 200000/300000 at levels 1..5, and
/// <c>binary</c> 200000 at level 1;</item>
/// <item>M2-optimal + M3-entropy + M4-splitter proofs: <c>text</c>/<c>binary</c>
/// 200000/300000 at levels 13..22 and every <c>code</c> vector at levels
/// 13..22 (the post-block splitter fires data-dependently on <c>code</c>;
/// the recursive entropy-estimation search, per-partition repcode
/// reconciliation and entropy staging all match);</item>
/// <item>pre-splitter proofs: heterogeneous 300000-byte frames whose second
/// 128 KiB chunk straddles a code/binary boundary (the fingerprint cut and
/// all downstream partitions match).</item>
/// </list>
/// Skipped (vacuous pass) when the toolchain is absent; fails on any byte
/// difference when present.
/// </summary>
public sealed class ParityVsNativeMultiblockTests
{
    private static byte[] MakeInput(string kind, int n, int seed)
    {
        // NOTE: deterministic arithmetic mix. System.HashCode.Combine is
        // randomized per process (even for integer inputs — verified: two
        // processes return different values for the same ints), so it must
        // never seed corpus generation; that would silently redraw every
        // vector on each run and make splitter-sensitive inclusions flaky.
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
                const string sample = "The quick brown fox jumps over the lazy dog. ZArchive block 64 KiB. ";
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
                // Heterogeneous frame for the pre-splitter
                // (ZSTD_optimalBlockSize): the second 128 KiB chunk straddles
                // a code/binary boundary, so the raw-byte fingerprint must cut
                // it short exactly like native.
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

    [Fact]
    public void MultiblockFraming_ByteIdenticalToNative()
    {
        var python = FindPythonWithZstd();
        if (python is null)
        {
            return;
        }

        var cases = new List<(int Level, string Kind, int Size)>();
        int[] allLevels =
        [
            1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13,
            14, 15, 16, 17, 18, 19, 20, 21, 22,
        ];
        int[] multiSizes = [65537, 100000, 131072, 131073, 200000, 300000];
        int[] singleOrTailSizes = [65537, 100000, 131072, 131073];

        foreach (var level in allLevels)
        {
            foreach (var size in multiSizes)
            {
                cases.Add((level, "random", size));
                cases.Add((level, "zeros", size));
            }

            foreach (var size in singleOrTailSizes)
            {
                cases.Add((level, "text", size));
                cases.Add((level, "binary", size));
            }
        }

        // Code past 64 KiB only where the post-block splitter is
        // structurally disabled (levels 1..12, below btopt; M4 covers 13+).
        for (var level = 1; level <= 12; level++)
        {
            foreach (var size in singleOrTailSizes)
            {
                cases.Add((level, "code", size));
            }
        }

        // M2 persistent-state proofs (fast through btlazy2): native reuses
        // cross-block matches with fresh-compatible entropy here.
        for (var level = 1; level <= 12; level++)
        {
            cases.Add((level, "text", 200000));
            cases.Add((level, "text", 300000));
        }

        for (var level = 1; level <= 5; level++)
        {
            cases.Add((level, "code", 200000));
            cases.Add((level, "code", 300000));
        }

        cases.Add((1, "binary", 200000));

        // M2 (optimal parsers) + M3 (entropy reuse) + M4 (splitter) proofs at
        // levels 13..22: text/binary never trigger the post-block splitter
        // on this corpus, and every code vector is covered (the splitter
        // fires data-dependently there). Seeds are fixed, so the
        // split/no-split decisions are stable.
        for (var level = 13; level <= 22; level++)
        {
            cases.Add((level, "text", 200000));
            cases.Add((level, "text", 300000));
            cases.Add((level, "binary", 200000));
            cases.Add((level, "binary", 300000));
        }

        for (var level = 13; level <= 22; level++)
        {
            foreach (var size in singleOrTailSizes)
            {
                cases.Add((level, "code", size));
            }

            foreach (var size in new[] { 200000, 300000 })
            {
                cases.Add((level, "code", size));
            }
        }

        // Pre-splitter proofs: heterogeneous 300000-byte frames whose second
        // 128 KiB chunk straddles a compressibility boundary (fast exercises
        // the borders heuristic, btlazy2+ the chunk scanner).
        foreach (var level in new[] { 1, 6, 13, 16, 19, 22 })
        {
            cases.Add((level, "hetero", 300000));
        }

        var work = Path.Combine(Path.GetTempPath(), "zar_mblk_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        try
        {
            var manifest = new List<string[]>();
            foreach (var (level, kind, size) in cases)
            {
                var input = MakeInput(kind, size, 42);
                var frame = new ZstdCompressor(ZstdCompressionOptions.FromLevel(level)).CompressBlock(input);
                var name = $"l{level}_{kind}_{size}";
                File.WriteAllBytes(Path.Combine(work, name + ".bin"), input);
                File.WriteAllBytes(Path.Combine(work, name + ".ours.zst"), frame);
                manifest.Add([name, name + ".bin", name + ".ours.zst", $"{level}"]);
            }

            File.WriteAllText(Path.Combine(work, "manifest.json"), JsonSerializer.Serialize(manifest));

            const string script =
                "import compression.zstd as z, json, os, sys\n" +
                "work = sys.argv[1]\n" +
                "manifest = json.load(open(os.path.join(work, 'manifest.json')))\n" +
                "fails = []\n" +
                "for name, b, o, lvl in manifest:\n" +
                "    data = open(os.path.join(work, b), 'rb').read()\n" +
                "    ours = open(os.path.join(work, o), 'rb').read()\n" +
                "    native = z.compress(data, level=int(lvl))\n" +
                "    if native != ours:\n" +
                "        fails.append(f'{name}: native {len(native)} ours {len(ours)}')\n" +
                "    elif z.decompress(ours) != data:\n" +
                "        fails.append(f'{name}: ours does not round-trip')\n" +
                "print(f'CHECKED {len(manifest)} FAILED {len(fails)}')\n" +
                "for f in fails:\n" +
                "    print('FAIL ' + f)\n" +
                "sys.exit(1 if fails else 0)\n";
            File.WriteAllText(Path.Combine(work, "check.py"), script);

            var psi = new ProcessStartInfo(python, $"\"{Path.Combine(work, "check.py")}\" \"{work}\"")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            using var proc = Process.Start(psi)!;
            var stdout = proc.StandardOutput.ReadToEnd();
            var stderr = proc.StandardError.ReadToEnd();
            var exited = proc.WaitForExit(600_000);
            Assert.True(exited, "native parity check timed out.\n" + stderr);
            Assert.True(proc.ExitCode == 0, $"Byte parity failed.\n{stdout}\n{stderr}");
            Assert.Contains($"CHECKED {manifest.Count} FAILED 0", stdout, StringComparison.Ordinal);
        }
        finally
        {
            try
            {
                Directory.Delete(work, recursive: true);
            }
            catch
            {
                // best effort
            }
        }
    }

    private static string? FindPythonWithZstd()
    {
        string[] candidates = OperatingSystem.IsWindows() ? ["py", "python", "python3"] : ["python3", "python"];
        foreach (var candidate in candidates)
        {
            try
            {
                var probe = Process.Start(new ProcessStartInfo(candidate, "-c \"import compression.zstd\"")
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
}
