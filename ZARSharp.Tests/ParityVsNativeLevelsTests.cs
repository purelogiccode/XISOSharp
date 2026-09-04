using System.Diagnostics;
using System.Text.Json;
using ZARSharp.Zstd;

namespace ZARSharp.Tests;

/// <summary>
/// PortPlan Step 3 acceptance: levels 1–22 single-shot frames (≤ 64 KiB, one
/// block) are byte-identical to native <c>ZSTD_compress(level)</c> (python
/// <c>compression.zstd</c>) across all size tiers and strategies
/// (fast/double-fast/greedy/lazy/lazy2/btlazy2/btopt/btultra/btultra2).
/// Skipped (vacuous pass) when the toolchain is absent; fails on any byte
/// difference when present.
/// </summary>
public sealed class ParityVsNativeLevelsTests
{
    private static byte[] MakeInput(string kind, int n, int seed)
    {
        var rng = new Random(HashCode.Combine(0x5332026, kind, n, seed));
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
            default:
                throw new ArgumentOutOfRangeException(nameof(kind));
        }

        return buf;
    }

    [Fact]
    public void Levels1To22_ByteIdenticalToNative()
    {
        var python = FindPythonWithZstd();
        if (python is null)
        {
            return;
        }

        int[] levels = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20, 21, 22];
        string[] kinds = ["text", "zeros", "random", "code", "binary"];
        int[] sizes = [0, 1, 8, 100, 256, 1000, 4096, 16384, 16385, 65536];

        var work = Path.Combine(Path.GetTempPath(), "zar_lvl_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        try
        {
            var manifest = new List<string[]>();
            foreach (var level in levels)
            {
                foreach (var kind in kinds)
                {
                    foreach (var size in sizes)
                    {
                        var input = MakeInput(kind, size, 42);
                        var frame = new ZstdCompressor(ZstdCompressionOptions.FromLevel(level)).CompressBlock(input);
                        var name = $"l{level}_{kind}_{size}";
                        File.WriteAllBytes(Path.Combine(work, name + ".bin"), input);
                        File.WriteAllBytes(Path.Combine(work, name + ".ours.zst"), frame);
                        manifest.Add([name, name + ".bin", name + ".ours.zst", $"{level}"]);
                    }
                }
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
