using System;
using System.Collections.Generic;

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
    internal static string OverwriteFlag(bool overwrite) => overwrite ? "-y" : "-n";

    internal static string[] Version() => ["-v"];

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

    internal static string[] List(IReadOnlyList<string> images) => ["-l", .. images];

    internal static string[] Tree(IReadOnlyList<string> images) => ["-t", .. images];

    internal static string[] Info(string image, string? path)
    {
        var args = new List<string> { "-i", image };
        if (!string.IsNullOrWhiteSpace(path))
        {
            args.Add(path);
        }

        return [.. args];
    }

    internal static string[] Unpack(string image, string? destDir)
    {
        var args = new List<string> { "--unpack", image };
        if (!string.IsNullOrWhiteSpace(destDir))
        {
            args.Add(destDir);
        }

        return [.. args];
    }

    internal static string[] CopyOut(string image, string imagePath, string dest)
        => ["--copy-out", image, imagePath, dest];

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
