using System.Diagnostics;
using System.Text.Json;
using ZARSharp.Zstd;

namespace ZARSharp.Tests;

/// <summary>
/// Phase 9 differential fuzz: seeded randomized inputs x sizes (crossing the
/// 64 KiB block boundary) x levels 1-6; every frame must satisfy
/// <c>decode(encode(x)) == x</c> via our decoder AND via native
/// <c>compression.zstd</c> (batched into one python process; skipped when the
/// toolchain is absent). Failing inputs are reproducible from the seed +
/// theory arguments printed on failure — no checked-in corpus needed.
/// Seed: 0x5EED2026.
/// </summary>
public sealed class ZstdEncoderFuzzTests
{
    private const int Seed = 0x5EED2026;

    public static TheoryData<int> Levels => new() { 1, 2, 3, 4, 5, 6 };

    public static TheoryData<int> Sizes => new()
    {
        0, 1, 2, 3, 7, 255, 256, 1023, 1024, 4096, 16384, 32768, 65535, 65536, 65537, 66000,
    };

    public static TheoryData<string> Kinds => new()
    {
        "zeros", "random", "text", "sparse", "pattern", "alternating", "allbytes",
    };

    private static byte[] MakeInput(string kind, int n, int level)
    {
        // Distinct stream per (kind, size, level) from the master seed.
        var rng = new Random(HashCode.Combine(Seed, kind, n, level));
        byte[] buf = new byte[n];
        switch (kind)
        {
            case "zeros":
                break;
            case "random":
                rng.NextBytes(buf);
                break;
            case "text":
            {
                const string sample = "The quick brown fox jumps over the lazy dog. Pack my box with five dozen liquor jugs. ";
                byte[] ascii = System.Text.Encoding.ASCII.GetBytes(sample);
                for (int i = 0; i < n; i++)
                {
                    buf[i] = ascii[(i + rng.Next(ascii.Length)) % ascii.Length];
                }

                break;
            }

            case "sparse":
                for (int i = 0; i < n / 97 + 1 && i * 97 < n; i++)
                {
                    buf[rng.Next(n == 0 ? 1 : n)] = (byte)rng.Next(256);
                }

                break;
            case "pattern":
            {
                byte[] period = new byte[rng.Next(1, 17)];
                rng.NextBytes(period);
                for (int i = 0; i < n; i++)
                {
                    buf[i] = period[i % period.Length];
                }

                break;
            }

            case "alternating":
                for (int i = 0; i < n; i++)
                {
                    buf[i] = (byte)(i % 2 == 0 ? 0x41 : 0xC3);
                }

                break;
            case "allbytes":
                for (int i = 0; i < n; i++)
                {
                    buf[i] = (byte)((i * 31 + rng.Next(256)) % 256);
                }

                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(kind));
        }

        return buf;
    }

    private static string CaseName(string kind, int n, int level) => $"{kind}/{n}b/L{level}";

    // Boundary levels on the full matrix; mid levels on a representative subset.
    public static TheoryData<string, int, int> FuzzCases()
    {
        var data = new TheoryData<string, int, int>();
        int[] sizes = [0, 1, 2, 3, 7, 255, 256, 1023, 1024, 4096, 16384, 32768, 65535, 65536, 65537, 66000];
        string[] kinds = ["zeros", "random", "text", "sparse", "pattern", "alternating", "allbytes"];
        foreach (string kind in kinds)
        {
            foreach (int size in sizes)
            {
                data.Add(kind, size, 1);
                data.Add(kind, size, 6);
            }
        }

        int[] midSizes = [255, 4096, 65536];
        string[] midKinds = ["text", "random", "zeros", "sparse"];
        foreach (string kind in midKinds)
        {
            foreach (int size in midSizes)
            {
                data.Add(kind, size, 2);
                data.Add(kind, size, 3);
                data.Add(kind, size, 4);
                data.Add(kind, size, 5);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(FuzzCases))]
    public void FuzzSelfRoundTrip(string kind, int size, int level)
    {
        byte[] input = MakeInput(kind, size, level);
        byte[] frame = new ZstdCompressor(ZstdCompressionOptions.FromLevel(level)).CompressBlock(input);
        byte[] decoded = ZstdCompressor.DecompressFrame(frame, Math.Max(size, 1));
        Assert.True(
            decoded.AsSpan().SequenceEqual(input),
            $"self round-trip failed for {CaseName(kind, size, level)}: frame {frame.Length} B");
    }

    [Fact]
    public void FuzzNativeDecodesOurFramesBatched()
    {
        string? python = FindPythonWithZstd();
        if (python is null)
        {
            return; // toolchain-conditional, like ZstdEncoderTests
        }

        // Representative subset (all kinds x boundary sizes x L1/L6): one frame
        // per case, all verified in a single python process.
        string[] kinds = ["zeros", "random", "text", "sparse", "pattern", "alternating", "allbytes"];
        int[] sizes = [0, 1, 255, 4096, 65536, 66000];
        int[] levels = [1, 6];

        string work = Path.Combine(Path.GetTempPath(), "zar_fuzz_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        try
        {
            var manifest = new List<(string Name, string Frame, string Input)>();
            foreach (string kind in kinds)
            {
                foreach (int size in sizes)
                {
                    foreach (int level in levels)
                    {
                        byte[] input = MakeInput(kind, size, level);
                        byte[] frame = new ZstdCompressor(ZstdCompressionOptions.FromLevel(level))
                            .CompressBlock(input);
                        string name = $"{kind}_{size}_{level}";
                        File.WriteAllBytes(Path.Combine(work, name + ".zst"), frame);
                        File.WriteAllBytes(Path.Combine(work, name + ".bin"), input);
                        manifest.Add((name, name + ".zst", name + ".bin"));
                    }
                }
            }

            File.WriteAllText(
                Path.Combine(work, "manifest.json"),
                JsonSerializer.Serialize(manifest.Select(m => new[] { m.Name, m.Frame, m.Input })));

            const string script =
                "import compression.zstd as z, json, os, sys\n" +
                "work = sys.argv[1]\n" +
                "manifest = json.load(open(os.path.join(work, 'manifest.json')))\n" +
                "fails = []\n" +
                "native_text_l6 = None\n" +
                "for name, f, x in manifest:\n" +
                "    frame = open(os.path.join(work, f), 'rb').read()\n" +
                "    want = open(os.path.join(work, x), 'rb').read()\n" +
                "    try:\n" +
                "        got = z.decompress(frame)\n" +
                "    except Exception as e:\n" +
                "        fails.append(f'{name}: decode error {e}')\n" +
                "        continue\n" +
                "    if got != want:\n" +
                "        fails.append(f'{name}: mismatch ({len(got)} != {len(want)})')\n" +
                "    if name == 'text_65536_6':\n" +
                "        native_text_l6 = len(z.compress(want, level=6))\n" +
                "        print(f'NATIVE_L6 {native_text_l6} OURS_L6 {len(frame)}')\n" +
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
            string stdout = proc.StandardOutput.ReadToEnd();
            string stderr = proc.StandardError.ReadToEnd();
            bool exited = proc.WaitForExit(300_000);
            Assert.True(exited, "native batch decode timed out.\n" + stderr);
            Assert.True(proc.ExitCode == 0, $"native batch decode failed.\n{stdout}\n{stderr}");
            Assert.True(
                stdout.Contains(
                    $"CHECKED {manifest.Count} FAILED 0", StringComparison.Ordinal),
                $"native batch count mismatch.\n{stdout}");

            // Phase 9 ratio gate: our text-64K L6 within 5% of native L6.
            foreach (string line in stdout.Split('\n'))
            {
                if (line.StartsWith("NATIVE_L6", StringComparison.Ordinal))
                {
                    string[] parts = line.Split(' ');
                    int native = int.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture);
                    int ours = int.Parse(parts[3], System.Globalization.CultureInfo.InvariantCulture);
                    Assert.True(
                        ours <= (long)(native * 1.05) + 64,
                        $"ratio regression: ours L6 {ours} B vs native L6 {native} B");
                }
            }
        }
        finally
        {
            try
            {
                Directory.Delete(work, recursive: true);
            }
            catch
            {
                /* best effort */
            }
        }
    }

    [Fact]
    public void RatioSanityWithoutToolchain()
    {
        // Always-on guards (no native oracle needed).
        var l6 = new ZstdCompressor(ZstdCompressionOptions.FromLevel(6));

        byte[] zeros = new byte[65536];
        Assert.True(l6.CompressBlock(zeros).Length < zeros.Length / 50, "zeros must collapse");

        byte[] random = MakeInput("random", 5000, 6);
        byte[] dest = new byte[ZstdCompressor.GetCompressBound(random.Length)];
        Assert.Equal(-1, l6.Compress(random, dest)); // decline => writer stores raw
        Assert.True(
            l6.CompressBlock(random).Length <= random.Length + 64,
            "incompressible must not expand beyond frame overhead");
    }

    private static string? FindPythonWithZstd()
    {
        string[] candidates = OperatingSystem.IsWindows()
            ? ["python", "python3"]
            : ["python3", "python"];
        foreach (string candidate in candidates)
        {
            try
            {
                var psi = new ProcessStartInfo(candidate, "-c \"import compression.zstd\"")
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                };
                using var proc = Process.Start(psi);
                if (proc is null)
                {
                    continue;
                }

                proc.WaitForExit(30_000);
                if (proc.ExitCode == 0)
                {
                    return candidate;
                }
            }
            catch
            {
                /* try next */
            }
        }

        return null;
    }
}
