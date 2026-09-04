namespace XISOSharp.Gui.Services;

/// <summary>
/// Pure builders for <c>XISOSharp</c> CLI argument lists. Flags always precede
/// positionals: the CLI treats unknown positionals as image paths, so an
/// option placed after an image (e.g. <c>-x game.cso -d out</c>) is opened
/// as a file instead of parsed as an option.
/// The GUI always passes <c>-y</c> or <c>-n</c> for commands that may prompt,
/// because there is no console to answer an interactive overwrite prompt.
/// </summary>
internal static class CliCommands
{
    /// <summary>
    /// Returns the non-interactive overwrite flag for CLI runs.
    /// </summary>
    /// <param name="overwrite"><c>true</c> for <c>-y</c> (overwrite); <c>false</c> for <c>-n</c> (refuse).</param>
    /// <returns><c>"-y"</c> or <c>"-n"</c>.</returns>
    internal static string OverwriteFlag(bool overwrite)
    {
        return overwrite ? "-y" : "-n";
    }

    /// <summary>
    /// Builds the <c>-v</c> version argv.
    /// </summary>
    /// <returns>The version argument list.</returns>
    internal static string[] Version()
    {
        return ["-v"];
    }

    /// <summary>
    /// Builds the extract argv (<c>-d</c> destination, <c>-x</c> images, overwrite flag).
    /// </summary>
    /// <param name="images">Image paths to extract.</param>
    /// <param name="destDir">Optional destination directory (<c>-d</c>).</param>
    /// <param name="overwrite">Whether to pass <c>-y</c> instead of <c>-n</c>.</param>
    /// <returns>The extract argument list.</returns>
    internal static string[] Extract(IReadOnlyList<string> images, string? destDir, bool overwrite)
    {
        var args = new List<string>();
        if (!string.IsNullOrWhiteSpace(destDir))
        {
            args.Add("-d");
            args.Add(destDir);
        }

        args.Add("-x");
        args.AddRange(images);
        args.Add(OverwriteFlag(overwrite));
        return [.. args];
    }

    /// <summary>
    /// Builds the list argv (<c>-l</c> plus images).
    /// </summary>
    /// <param name="images">Image paths to list.</param>
    /// <returns>The list argument list.</returns>
    internal static string[] List(IReadOnlyList<string> images)
    {
        return ["-l", .. images];
    }

    /// <summary>
    /// Builds the tree argv (<c>-t</c> plus images).
    /// </summary>
    /// <param name="images">Image paths to show as a tree.</param>
    /// <returns>The tree argument list.</returns>
    internal static string[] Tree(IReadOnlyList<string> images)
    {
        return ["-t", .. images];
    }

    /// <summary>
    /// Builds the info argv (<c>-i</c> image plus optional in-image path).
    /// </summary>
    /// <param name="image">Image path.</param>
    /// <param name="path">Optional directory path inside the image.</param>
    /// <returns>The info argument list.</returns>
    internal static string[] Info(string image, string? path)
    {
        var args = new List<string> { "-i", image };
        if (!string.IsNullOrWhiteSpace(path))
        {
            args.Add(path);
        }

        return [.. args];
    }

    /// <summary>
    /// Builds the unpack argv (<c>--unpack</c> image plus optional destination).
    /// </summary>
    /// <param name="image">Image path.</param>
    /// <param name="destDir">Optional destination directory.</param>
    /// <returns>The unpack argument list.</returns>
    internal static string[] Unpack(string image, string? destDir)
    {
        var args = new List<string> { "--unpack", image };
        if (!string.IsNullOrWhiteSpace(destDir))
        {
            args.Add(destDir);
        }

        return [.. args];
    }

    /// <summary>
    /// Builds the copy-out argv (<c>--copy-out</c> image, in-image path, destination).
    /// </summary>
    /// <param name="image">Image path.</param>
    /// <param name="imagePath">Path of the file inside the image.</param>
    /// <param name="dest">Destination path on disk.</param>
    /// <returns>The copy-out argument list.</returns>
    internal static string[] CopyOut(string image, string imagePath, string dest)
    {
        return ["--copy-out", image, imagePath, dest];
    }

    /// <summary>
    /// Builds the create argv (<c>-c</c> source, optional name, <c>-X</c> excludes, <c>-s</c>/<c>-m</c>).
    /// </summary>
    /// <param name="sourceDir">Source directory to pack.</param>
    /// <param name="name">Optional output name.</param>
    /// <param name="excludes">Exclude patterns, each emitted as <c>-X</c>.</param>
    /// <param name="skipSystemUpdate">Whether to pass <c>-s</c>.</param>
    /// <param name="disableXbePatch">Whether to pass <c>-m</c>.</param>
    /// <param name="overwrite">Whether to pass <c>-y</c> instead of <c>-n</c>.</param>
    /// <returns>The create argument list.</returns>
    internal static string[] Create(
        string sourceDir,
        string? name,
        IReadOnlyList<string> excludes,
        bool skipSystemUpdate,
        bool disableXbePatch,
        bool overwrite)
    {
        var args = new List<string> { "-c", sourceDir };
        if (!string.IsNullOrWhiteSpace(name))
        {
            args.Add(name);
        }

        foreach (var exclude in excludes)
        {
            if (!string.IsNullOrWhiteSpace(exclude))
            {
                args.Add("-X");
                args.Add(exclude);
            }
        }

        if (skipSystemUpdate)
        {
            args.Add("-s");
        }

        if (disableXbePatch)
        {
            args.Add("-m");
        }

        args.Add(OverwriteFlag(overwrite));
        return [.. args];
    }

    /// <summary>
    /// Builds the rewrite argv (<c>-r</c> with <c>-d</c>/<c>-o</c>/<c>-D</c>/<c>-m</c> and validate flags).
    /// </summary>
    /// <param name="images">Image paths to rewrite.</param>
    /// <param name="output">Optional <c>-o</c> output path.</param>
    /// <param name="workDir">Optional <c>-d</c> work directory.</param>
    /// <param name="deleteOld">Whether to pass <c>-D</c>.</param>
    /// <param name="disableXbePatch">Whether to pass <c>-m</c>.</param>
    /// <param name="validate">Whether to pass <c>--validate</c>.</param>
    /// <param name="validateChecksums">Whether to pass <c>--validate-checksums</c> instead.</param>
    /// <param name="validateStrict">Whether to pass <c>--validate-strict</c>.</param>
    /// <param name="validateReport">Optional <c>--validate-report</c> path.</param>
    /// <param name="overwrite">Whether to pass <c>-y</c> instead of <c>-n</c>.</param>
    /// <returns>The rewrite argument list.</returns>
    internal static string[] Rewrite(
        IReadOnlyList<string> images,
        string? output,
        string? workDir,
        bool deleteOld,
        bool disableXbePatch,
        bool validate,
        bool validateChecksums,
        bool validateStrict,
        string? validateReport,
        bool overwrite)
    {
        var args = new List<string> { "-r" };
        if (!string.IsNullOrWhiteSpace(workDir))
        {
            args.Add("-d");
            args.Add(workDir);
        }

        if (!string.IsNullOrWhiteSpace(output))
        {
            args.Add("-o");
            args.Add(output);
        }

        if (deleteOld)
        {
            args.Add("-D");
        }

        if (disableXbePatch)
        {
            args.Add("-m");
        }

        if (validateChecksums)
        {
            args.Add("--validate-checksums");
        }
        else if (validate)
        {
            args.Add("--validate");
        }

        if (validateStrict)
        {
            args.Add("--validate-strict");
        }

        if (!string.IsNullOrWhiteSpace(validateReport))
        {
            args.Add("--validate-report");
            args.Add(validateReport);
        }

        args.AddRange(images);
        args.Add(OverwriteFlag(overwrite));
        return [.. args];
    }

    /// <summary>
    /// Builds the wipe argv (<c>--wipe</c> image plus optional output).
    /// </summary>
    /// <param name="image">Image path.</param>
    /// <param name="output">Optional output path.</param>
    /// <param name="overwrite">Whether to pass <c>-y</c> instead of <c>-n</c>.</param>
    /// <returns>The wipe argument list.</returns>
    internal static string[] Wipe(string image, string? output, bool overwrite)
    {
        var args = new List<string> { "--wipe", image };
        if (!string.IsNullOrWhiteSpace(output))
        {
            args.Add(output);
        }

        args.Add(OverwriteFlag(overwrite));
        return [.. args];
    }

    /// <summary>
    /// Builds the trim argv (<c>--trim</c> image plus optional output).
    /// </summary>
    /// <param name="image">Image path.</param>
    /// <param name="output">Optional output path.</param>
    /// <param name="overwrite">Whether to pass <c>-y</c> instead of <c>-n</c>.</param>
    /// <returns>The trim argument list.</returns>
    internal static string[] Trim(string image, string? output, bool overwrite)
    {
        var args = new List<string> { "--trim", image };
        if (!string.IsNullOrWhiteSpace(output))
        {
            args.Add(output);
        }

        args.Add(OverwriteFlag(overwrite));
        return [.. args];
    }

    /// <summary>
    /// Builds the rebuild argv (<c>rebuild</c> parts, <c>-o</c> output, optional sectors file).
    /// </summary>
    /// <param name="parts">Redump component paths.</param>
    /// <param name="output">Output Redump ISO path.</param>
    /// <param name="securitySectors">Optional <c>--security-sectors</c> path.</param>
    /// <param name="overwrite">Whether to pass <c>-y</c> instead of <c>-n</c>.</param>
    /// <returns>The rebuild argument list.</returns>
    internal static string[] Rebuild(
        IReadOnlyList<string> parts,
        string output,
        string? securitySectors,
        bool overwrite)
    {
        var args = new List<string> { "rebuild" };
        args.AddRange(parts);
        args.Add("-o");
        args.Add(output);
        if (!string.IsNullOrWhiteSpace(securitySectors))
        {
            args.Add("--security-sectors");
            args.Add(securitySectors);
        }

        args.Add(OverwriteFlag(overwrite));
        return [.. args];
    }

    /// <summary>
    /// Builds the compress argv (<c>compress</c> with level, version, optional split and output).
    /// </summary>
    /// <param name="source">Source image or directory.</param>
    /// <param name="output">Optional output path.</param>
    /// <param name="level">CISO compression level.</param>
    /// <param name="version">CISO version (1 or 2).</param>
    /// <param name="splitBytes">Optional <c>--ciso-split</c> value.</param>
    /// <param name="overwrite">Whether to pass <c>-y</c> instead of <c>-n</c>.</param>
    /// <returns>The compress argument list.</returns>
    internal static string[] Compress(
        string source,
        string? output,
        int level,
        int version,
        string? splitBytes,
        bool overwrite)
    {
        var args = new List<string>
        {
            "compress",
            "--ciso-level",
            level.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "--ciso-version",
            version.ToString(System.Globalization.CultureInfo.InvariantCulture),
        };
        if (!string.IsNullOrWhiteSpace(splitBytes))
        {
            args.Add("--ciso-split");
            args.Add(splitBytes);
        }

        args.Add(source);
        if (!string.IsNullOrWhiteSpace(output))
        {
            args.Add(output);
        }

        args.Add(OverwriteFlag(overwrite));
        return [.. args];
    }

    /// <summary>
    /// Builds the decompress argv (<c>decompress</c> CSO plus optional output).
    /// </summary>
    /// <param name="cso">Source CSO path.</param>
    /// <param name="output">Optional output ISO path.</param>
    /// <param name="overwrite">Whether to pass <c>-y</c> instead of <c>-n</c>.</param>
    /// <returns>The decompress argument list.</returns>
    internal static string[] Decompress(string cso, string? output, bool overwrite)
    {
        var args = new List<string> { "decompress", cso };
        if (!string.IsNullOrWhiteSpace(output))
        {
            args.Add(output);
        }

        args.Add(OverwriteFlag(overwrite));
        return [.. args];
    }

    /// <summary>
    /// Builds the validate argv (<c>validate</c> with optional checksums flag and report).
    /// </summary>
    /// <param name="source">Source ISO path.</param>
    /// <param name="output">Output ISO path.</param>
    /// <param name="checksums">Whether to pass <c>--validate-checksums</c>.</param>
    /// <param name="report">Optional <c>--validate-report</c> path.</param>
    /// <returns>The validate argument list.</returns>
    internal static string[] Validate(string source, string output, bool checksums, string? report)
    {
        var args = new List<string> { "validate" };
        if (checksums)
        {
            args.Add("--validate-checksums");
        }

        if (!string.IsNullOrWhiteSpace(report))
        {
            args.Add("--validate-report");
            args.Add(report);
        }

        args.Add(source);
        args.Add(output);
        return [.. args];
    }

    /// <summary>
    /// Builds the checksum argv (<c>checksum</c> images plus optional <c>--silent</c>).
    /// </summary>
    /// <param name="images">Image paths to checksum.</param>
    /// <param name="silent">Whether to pass <c>--silent</c>.</param>
    /// <returns>The checksum argument list.</returns>
    internal static string[] Checksum(IReadOnlyList<string> images, bool silent)
    {
        var args = new List<string> { "checksum" };
        args.AddRange(images);
        if (silent)
        {
            args.Add("--silent");
        }

        return [.. args];
    }

    /// <summary>
    /// Builds the batch argv (<c>--batch</c> directory with recursion, mode flag, and destination).
    /// </summary>
    /// <param name="dir">Directory to scan for images.</param>
    /// <param name="recursive">Whether to pass <c>--batch-recursive</c>.</param>
    /// <param name="modeFlag">Mode flag such as <c>-x</c>, <c>-l</c>, <c>-t</c>, <c>-r</c>, or <c>-V</c>.</param>
    /// <param name="destDir">Optional <c>-d</c> destination directory.</param>
    /// <param name="overwrite">Whether to pass <c>-y</c> instead of <c>-n</c>.</param>
    /// <returns>The batch argument list.</returns>
    internal static string[] Batch(
        string dir,
        bool recursive,
        string modeFlag,
        string? destDir,
        bool overwrite)
    {
        ArgumentException.ThrowIfNullOrEmpty(modeFlag);
        var args = new List<string> { "--batch", dir };
        if (recursive)
        {
            args.Add("--batch-recursive");
        }

        if (!string.IsNullOrWhiteSpace(destDir))
        {
            args.Add("-d");
            args.Add(destDir);
        }

        args.Add(modeFlag);
        args.Add(OverwriteFlag(overwrite));
        return [.. args];
    }
}