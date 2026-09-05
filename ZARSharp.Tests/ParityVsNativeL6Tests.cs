using System.Diagnostics;
using System.Text.Json;
using ZARSharp.Zstd;

namespace ZARSharp.Tests;

/// <summary>
/// PortPlan Step 2 acceptance: level-6 single-shot frames (≤ 64 KiB, one block)
/// are byte-identical to native <c>ZSTD_compress(level 6)</c> (python
/// <c>compression.zstd</c>). Skipped (vacuous pass) when the toolchain is
/// absent; fails on any byte difference when present. Ratio gate is equality
/// (≤1.00×), not the old 1.05×.
/// </summary>
public sealed class ParityVsNativeL6Tests
{
    private static byte[] MakeInput(string kind, int n, int seed)
    {
        // NOTE: deterministic arithmetic mix. System.HashCode.Combine carries
        // a per-process random seed (even for integer inputs), so it must
        // never seed corpus generation; that would silently redraw every
        // vector on each run and make failures unreproducible.
        var kindIndex = kind switch
        {
            "zeros" => 0,
            "random" => 1,
            "text" => 2,
            "pattern" => 3,
            "code" => 4,
            "binary" => 5,
            "period2" => 6,
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
        var rng = new Random(unchecked((int)(0x5162026u + (uint)kindIndex * 0x9E3779B9u + (uint)n * 31u + (uint)seed * 131u)));
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

            case "pattern":
                for (var i = 0; i < n; i++)
                {
                    buf[i] = (byte)((i * 31 + 7) % 256);
                }

                break;
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
            case "period2":
                for (var i = 0; i < n; i++)
                {
                    buf[i] = (byte)(i % 2);
                }

                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(kind));
        }

        return buf;
    }

    [Fact]
    public void L6_SingleBlock_ByteIdenticalToNative()
    {
        var python = FindPythonWithZstd();
        if (python is null)
        {
            return;
        }

        string[] kinds = ["text", "zeros", "random", "pattern", "code", "binary", "period2"];
        int[] sizes = [0, 1, 2, 7, 8, 31, 100, 255, 256, 1000, 4096, 16383, 16384, 16385, 32768, 65535, 65536];

        var work = Path.Combine(Path.GetTempPath(), "zar_l6_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        try
        {
            var manifest = new List<string[]>();
            foreach (var kind in kinds)
            {
                foreach (var size in sizes)
                {
                    var input = MakeInput(kind, size, 42);
                    var frame = new ZstdCompressor(ZstdCompressionOptions.FromLevel(6)).CompressBlock(input);
                    var name = $"{kind}_{size}";
                    File.WriteAllBytes(Path.Combine(work, name + ".bin"), input);
                    File.WriteAllBytes(Path.Combine(work, name + ".ours.zst"), frame);
                    manifest.Add([name, name + ".bin", name + ".ours.zst"]);
                }
            }

            File.WriteAllText(Path.Combine(work, "manifest.json"), JsonSerializer.Serialize(manifest));

            const string script =
                "import compression.zstd as z, json, os, sys\n" +
                "work = sys.argv[1]\n" +
                "manifest = json.load(open(os.path.join(work, 'manifest.json')))\n" +
                "fails = []\n" +
                "for name, b, o in manifest:\n" +
                "    data = open(os.path.join(work, b), 'rb').read()\n" +
                "    ours = open(os.path.join(work, o), 'rb').read()\n" +
                "    native = z.compress(data, level=6)\n" +
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
            var exited = proc.WaitForExit(300_000);
            Assert.True(exited, "native L6 parity check timed out.\n" + stderr);
            Assert.True(proc.ExitCode == 0, $"L6 byte parity failed.\n{stdout}\n{stderr}");
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
        var extra = OperatingSystem.IsWindows() ? "py -3" : null;
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

        if (extra is not null)
        {
            try
            {
                var parts = extra.Split(' ', 2);
                var probe = Process.Start(new ProcessStartInfo(parts[0], parts[1] + " -c \"import compression.zstd\"")
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                });
                if (probe is not null)
                {
                    probe.WaitForExit(15_000);
                    if (probe.ExitCode == 0)
                    {
                        return extra;
                    }
                }
            }
            catch
            {
                // absent
            }
        }

        return null;
    }
}
