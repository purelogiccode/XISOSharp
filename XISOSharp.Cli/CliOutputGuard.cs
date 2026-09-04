using XISOSharp;

namespace XISOSharp.Cli;

/// <summary>
/// Input==output refusal checks (TODO #15, xdvdfs #36). Each check returns the
/// error line to print, or <c>null</c> when the output is safe. Kept in one
/// place so the per-mode wiring stays a two-liner and the rules are unit
/// testable; the streaming library APIs enforce the same rules as a backstop
/// (they throw <see cref="IOException"/>), but these pre-checks fire before
/// any prompt, move, or byte is touched.
/// </summary>
internal static class CliOutputGuard
{
    /// <summary>
    /// Flags the upstream #61 confusion: a known option spelling sitting in a
    /// positional slot (the main parser stops at the first non-flag token, so
    /// <c>game.iso -d ./new/</c> would otherwise be probed as a file named
    /// <c>-d</c>). Returns the error line, or <c>null</c> for anything that is
    /// not an exact known-flag spelling. Callers still let a token through
    /// when it exists on disk, so a file literally named like a flag keeps
    /// working.
    /// </summary>
    public static string? CheckMisplacedFlag(string? token)
    {
        if (string.IsNullOrEmpty(token) || !MisplacedFlags.Contains(token))
            return null;

        return $"Error: {token} must come before ISO filenames" +
               $" (e.g. -x {token} <value> game.iso);" +
               " a flag after the first filename is read as a filename\n";
    }

    private static readonly HashSet<string> MisplacedFlags = new(StringComparer.Ordinal)
    {
        "-v", "-h", "-c", "-x", "--unpack", "-X", "-l", "-t", "-i",
        "--ls", "--xex-info", "--md5", "--sha256", "-V", "validate",
        "--validate", "--validate-checksums", "--validate-strict",
        "--validate-report", "--copy-out", "-r", "-q", "-Q", "-s", "-D",
        "-m", "-y", "--yes", "-n", "--no", "-d", "-o", "-O", "--output",
        "-p", "--skip-sectors", "--prepend-sectors", "--batch",
        "--batch-recursive", "--skip-existing", "--continue-on-error", "--pack", "--video",
        "--random", "--seed", "--wipe", "--trim", "--petrify", "--update",
        "--zar", "--all", "--best", "--compress", "--security-sectors",
        "--sectors", "--checksum", "--filetime", "--get-filetime",
        "--set-filetime", "--silent", "--dry-run",
    };

    /// <summary>
    /// Rewrite (<c>-r</c>) runs <c>input → input.old</c> before writing, so an
    /// <c>-o</c> pointing at the input itself (or at the backup about to hold
    /// it) is refused: the former is just the default, the latter destroys data.
    /// </summary>
    public static string? CheckRewriteOutput(string xisoPath, string? outputName)
    {
        if (string.IsNullOrWhiteSpace(outputName))
            return null;

        if (XisoPaths.AreSamePath(xisoPath, outputName))
        {
            return $"Error: rewrite output {outputName} is the same file as the input;" +
                   " omit -o to rewrite in place\n";
        }

        if (XisoPaths.AreSamePath(xisoPath + ".old", outputName))
        {
            return $"Error: rewrite output {outputName} would overwrite the {xisoPath}.old backup;" +
                   " choose another name\n";
        }

        return null;
    }

    /// <summary>
    /// Single-input <c>-o</c> used by the redump batch modes (video/random/seed/
    /// wipe/trim/petrify/update/zar): the output must not be the input itself.
    /// </summary>
    public static string? CheckSingleInputOutput(string input, string? outputName)
    {
        if (string.IsNullOrWhiteSpace(outputName))
            return null;

        if (XisoPaths.AreSamePath(input, outputName))
        {
            return $"Error: -o output {outputName} is the same file as the input {input};" +
                   " choose another name\n";
        }

        return null;
    }

    /// <summary>
    /// Rebuild output must not clobber any component (xiso, video, filler/seed,
    /// update) nor the sectors file it is read from while writing.
    /// </summary>
    public static string? CheckRebuildOutput(string output, string? securitySectorsPath,
        params string?[] parts)
    {
        foreach (var part in parts)
        {
            if (!string.IsNullOrWhiteSpace(part) && XisoPaths.AreSamePath(part, output))
            {
                return $"Error: rebuild output {output} is the same file as input {part};" +
                       " choose another name\n";
            }
        }

        if (!string.IsNullOrWhiteSpace(securitySectorsPath) &&
            XisoPaths.AreSamePath(securitySectorsPath, output))
        {
            return $"Error: rebuild output {output} is the same file as the sectors file {securitySectorsPath};" +
                   " choose another name\n";
        }

        return null;
    }

    /// <summary>
    /// Compress/decompress output (explicit or derived default) must not be the
    /// source file itself. Split-part collisions beyond the base name are caught
    /// by the library backstop (<see cref="CisoWriter.CompressToCso"/>).
    /// </summary>
    public static string? CheckImageOutput(string source, string output)
    {
        if (XisoPaths.AreSamePath(source, output))
        {
            return $"Error: output {output} is the same file as the input {source};" +
                   " choose another name\n";
        }

        return null;
    }
}