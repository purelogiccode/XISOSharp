using System.Diagnostics;

namespace ZARSharp.Tests;

/// <summary>
/// Builds the <c>seekoracle</c> ground-truth binary (committed C source over
/// the frozen libzstd 1.5.7 streaming API, zeekstd framing semantics) into the
/// temp dir, once per machine. Returns null when gcc is absent or the build
/// fails, in which case oracle parity tests skip (vacuous pass).
/// </summary>
internal static class SeekOracle
{
    private static readonly string? Exe = Build();

    public static string? ExePath => Exe;

    private static string? Build()
    {
        var dir = Path.Combine(Path.GetTempPath(), "zarsharp-seekoracle");
        var exe = Path.Combine(dir, "seekoracle.exe");
        try
        {
            var repo = FindRepoRoot();
            if (repo is null)
            {
                return null;
            }

            var lib = Path.Combine(repo, "References", "zstd-1.5.7", "lib");
            var oracleC = Path.Combine(repo, "ZARSharp.Tests", "SeekOracle", "seekoracle.c");
            string[] libSources =
            [
                "compress/zstd_compress.c", "compress/zstd_compress_literals.c",
                "compress/zstd_compress_sequences.c", "compress/zstd_compress_superblock.c",
                "compress/zstd_double_fast.c", "compress/zstd_fast.c",
                "compress/zstd_lazy.c", "compress/zstd_opt.c",
                "compress/zstd_ldm.c", "compress/zstd_preSplit.c",
                "compress/fse_compress.c", "compress/hist.c", "compress/huf_compress.c",
                "common/entropy_common.c", "common/error_private.c", "common/xxhash.c",
                "common/zstd_common.c", "common/fse_decompress.c",
                "decompress/huf_decompress.c", "decompress/huf_decompress_amd64.S",
                "decompress/zstd_ddict.c", "decompress/zstd_decompress.c",
                "decompress/zstd_decompress_block.c",
            ];
            var inputs = libSources.Select(s => Path.Combine(lib, s)).Append(oracleC).ToArray();
            if (inputs.Any(f => !File.Exists(f)))
            {
                return null;
            }

            var exeTime = File.Exists(exe) ? File.GetLastWriteTimeUtc(exe) : DateTime.MinValue;
            if (inputs.All(f => File.GetLastWriteTimeUtc(f) <= exeTime))
            {
                return exe;
            }

            Directory.CreateDirectory(dir);
            var args = $"-O1 -o \"{exe}\" -I\"{lib}\" -I\"{Path.Combine(lib, "common")}\" "
                + "-DZSTD_LEGACY_SUPPORT=0 "
                + string.Join(" ", inputs.Select(f => $"\"{f}\""));
            using var proc = Process.Start(new ProcessStartInfo
            {
                FileName = "gcc",
                Arguments = args,
                RedirectStandardError = true,
                UseShellExecute = false,
            });
            if (proc is null)
            {
                return null;
            }

            proc.WaitForExit(600000);
            if (proc.ExitCode != 0 || !File.Exists(exe))
            {
                return null;
            }

            return exe;
        }
        catch
        {
            return null;
        }
    }

    private static string? FindRepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir, "CSharp_XISOSharp.sln")))
            {
                return dir;
            }

            dir = Directory.GetParent(dir)?.FullName;
        }

        return null;
    }
}
