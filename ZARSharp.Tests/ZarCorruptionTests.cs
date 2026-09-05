using ZARSharp.Pipeline;
using ZARSharp.Zstd;

namespace ZARSharp.Tests;

/// <summary>
/// Archive-level corruption battery. Core property: a truncated or flipped
/// <c>.zar</c> must either throw a documented exception or extract — never
/// hang, never escape <see cref="OutOfMemoryException"/> on untrusted sizes.
/// Every case runs under a 10 s guard (same idiom as
/// <c>ZstdCorruptionTests</c>). Assertion strength:
/// <list type="bullet">
/// <item>truncations (any proper prefix, incl. empty): MUST throw — the
/// footer/TOC lives at the end, so open fails with
/// <see cref="ZarArchiveOpenException"/>;</item>
/// <item>flips anywhere (header, data, TOC, footer): throw-or-complete —
/// neither implementation verifies integrity (the writer hashes, neither
/// reader checks; data blocks carry no checksums), so a flip may decode to
/// different bytes, which is legitimate, not silent corruption.</item>
/// </list>
/// </summary>
public sealed class ZarCorruptionTests : IDisposable
{
    private readonly List<string> _tempDirs = [];

    public void Dispose()
    {
        foreach (var dir in _tempDirs)
        {
            try
            {
                if (Directory.Exists(dir))
                {
                    Directory.Delete(dir, true);
                }
            }
            catch
            {
                // ignored
            }
        }
    }

    private string NewTempDir(string prefix)
    {
        var dir = Path.Combine(Path.GetTempPath(), "zarsharp", prefix + "_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        _tempDirs.Add(dir);
        return dir;
    }

    private static byte[] MakeArchive(string root)
    {
        var src = Directory.CreateDirectory(Path.Combine(root, "src")).FullName;
        var rng = new Random(unchecked((int)0xC04E10CE));
        var random = new byte[3000];
        rng.NextBytes(random);
        File.WriteAllBytes(Path.Combine(src, "data.bin"), random);
        File.WriteAllText(Path.Combine(src, "readme.txt"), "corruption battery " + new string('x', 500));
        var zar = Path.Combine(root, "arch.zar");
        ZarPipeline.Pack(src, zar);
        return File.ReadAllBytes(zar);
    }

    /// <summary>Extracts under a hang guard. Returns the captured exception, or null on success.</summary>
    private static Exception? GuardedExtract(byte[] bytes, string workDir, string label)
    {
        Exception? captured = null;
        var task = Task.Run(() =>
        {
            try
            {
                var zar = Path.Combine(workDir, "case.zar");
                File.WriteAllBytes(zar, bytes);
                ZarPipeline.Extract(zar, Path.Combine(workDir, "out"));
            }
            catch (Exception ex)
            {
                captured = ex;
            }
        });
        var finished = task.Wait(TimeSpan.FromSeconds(10));
        Assert.True(finished, $"extract hung on corrupted input ({label})");
        Assert.False(captured is OutOfMemoryException, $"OOM on corrupted input ({label})");
        return captured;
    }

    private static void AssertDocumented(Exception? captured, string label)
    {
        Assert.NotNull(captured);
        Assert.True(
            captured is ZarArchiveOpenException or InvalidOperationException or IOException
                or UnauthorizedAccessException or ZstdException,
            $"undocumented {captured!.GetType().Name} at {label}: {captured.Message}");
    }

    [Fact]
    public void Truncations_AlwaysThrow()
    {
        var root = NewTempDir("zar_corr_trunc");
        var work = Directory.CreateDirectory(Path.Combine(root, "work")).FullName;
        var archive = MakeArchive(root);

        var offsets = new HashSet<int>();
        for (var i = 0; i < Math.Min(128, archive.Length); i++)
        {
            offsets.Add(i);
        }

        for (var i = Math.Max(0, archive.Length - 128); i < archive.Length; i++)
        {
            offsets.Add(i);
        }

        for (var i = 0; i < archive.Length; i += 32)
        {
            offsets.Add(i);
        }

        foreach (var cut in offsets)
        {
            var prefix = archive.AsSpan(0, cut).ToArray();
            var captured = GuardedExtract(prefix, work, $"trunc {cut}");
            Assert.NotNull(captured);
            Assert.IsType<ZarArchiveOpenException>(captured);
        }
    }

    [Fact]
    public void Flips_ThrowOrComplete_NeverHangOrOom()
    {
        var root = NewTempDir("zar_corr_flip");
        var work = Directory.CreateDirectory(Path.Combine(root, "work")).FullName;
        var archive = MakeArchive(root);

        var offsets = new HashSet<int>();
        for (var off = 0; off < Math.Min(64, archive.Length); off++)
        {
            offsets.Add(off);
        }

        for (var off = 64; off < archive.Length; off += 32)
        {
            offsets.Add(off);
        }

        foreach (var off in offsets)
        {
            var corrupted = (byte[])archive.Clone();
            corrupted[off] ^= 0xFF;
            var captured = GuardedExtract(corrupted, work, $"flip {off}");
            if (captured is not null)
            {
                AssertDocumented(captured, $"flip {off}");
            }
        }

        // A couple of multi-byte hits in the tail (TOC/footer region).
        foreach (var off in new[] { archive.Length - 8, archive.Length - 64 })
        {
            if (off < 0)
            {
                continue;
            }

            var corrupted = (byte[])archive.Clone();
            corrupted[off] ^= 0xFF;
            corrupted[off + 1] ^= 0x0F;
            var captured = GuardedExtract(corrupted, work, $"tail flip {off}");
            if (captured is not null)
            {
                AssertDocumented(captured, $"tail flip {off}");
            }
        }
    }
}
