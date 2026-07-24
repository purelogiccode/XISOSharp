using System.Text;

namespace XISOSharp.Cli;

/// <summary>
/// Command-line entry point for extract-xiso.
/// Parses arguments and dispatches to <see cref="XisoReader"/> for extraction/listing/rewriting
/// or <see cref="XisoWriter"/> for image creation.
/// </summary>
internal static class Program
{
    /// <summary>
    /// Entry point. Parses command-line flags and positional arguments,
    /// then invokes the appropriate XISO operation.
    /// </summary>
    /// <param name="args">Command-line arguments.</param>
    /// <returns>0 on success, 1 on error.</returns>
    private static int Main(string[] args)
    {
        if (args.Length < 1)
        {
            PrintUsage();
            return 1;
        }

        var extract = true;
        var rewrite = false;
        var xSeen = false;
        var deleteOld = false;
        string? path = null;
        var createList = new List<(string Dir, string? Name)>();
        var isos = 0;
        var err = 0;

        var optind = 0;
        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (arg.StartsWith('-') && arg.Length > 1)
            {
                switch (arg)
                {
                    case "-v":
                        Console.Write(Constants.Banner);
                        return 0;
                    case "-h":
                        PrintUsage();
                        return 0;
                    case "-c":
                        {
                            if (xSeen || rewrite || !extract || i + 1 >= args.Length) { PrintUsage();
                                return 1; }

                            var dir = args[++i];
                            string? name = null;
                            if (i + 1 < args.Length && !args[i + 1].StartsWith('-'))
                            {
                                name = args[++i];
                            }

                            createList.Add((dir, name));
                            break;
                        }
                    case "-x": xSeen = true; break;
                    case "-l":
                        if (xSeen || rewrite || createList.Count > 0) { PrintUsage();
                            return 1; }
                        extract = false;
                        break;
                    case "-r":
                        if (xSeen || !extract || createList.Count > 0) { PrintUsage();
                            return 1; }
                        rewrite = true;
                        break;
                    case "-q": Logger.Quiet = true; break;
                    case "-Q": Logger.Quiet = Logger.RealQuiet = true; break;
                    case "-s": Logger.RemoveSystemUpdate = true; break;
                    case "-D": deleteOld = true; break;
                    case "-m": Logger.MediaEnable = false; break;
                    case "-d":
                        if (i + 1 < args.Length)
                        {
                            path = args[++i];
                        }
                        else { PrintUsage();
                            return 1; }
                        break;
                    case "-p":
                        PrintUsage();
                        return 1;
                    default:
                        optind = i;
                        goto parse_done;
                }
                optind = i + 1;
            }
            else
            {
                optind = i;
                break;
            }
        }

        parse_done:

        if (createList.Count > 0)
        {
            if (optind < args.Length) { PrintUsage();
                return 1; }
        }
        else if (optind >= args.Length)
        {
            PrintUsage();
            return 1;
        }

        Logger.Log(Constants.Banner);

        if (createList.Count > 0)
        {
            foreach ((string dir, string? name) in createList)
            {
                string? outputDir = null;
                string? isoName = null;

                if (name != null)
                {
                    var lastSep = name.LastIndexOf(Constants.PathChar);
                    if (lastSep >= 0)
                    {
                        outputDir = name[..lastSep];
                        isoName = name[(lastSep + 1)..];
                    }
                    else
                    {
                        isoName = name;
                    }
                }

                XisoWriter.CreateXiso(dir, outputDir, null, null, out _, isoName, null);
            }
            return 0;
        }

        for (var i = optind; i < args.Length; i++)
        {
            isos++;
            Logger.Log("\n");
            Logger.TotalBytes = Logger.TotalFiles = 0;

            var xisoPath = args[i];
            var optimized = false;

            try
            {
                using var tagFs = new FileStream(xisoPath, new FileStreamOptions
                {
                    Mode = FileMode.Open,
                    Access = FileAccess.Read,
                    Share = FileShare.Read,
                    BufferSize = 256
                });

                tagFs.Seek(Constants.OptimizedTagOffset, SeekOrigin.Begin);
                var tagBuf = new byte[Constants.OptimizedTagLength];
                var tagRead = tagFs.Read(tagBuf);
                if (tagRead == Constants.OptimizedTagLength)
                {
                    var tag = Encoding.ASCII.GetString(tagBuf);
                    if (tag.StartsWith(Constants.OptimizedTag[..Constants.OptimizedTagLengthMin], StringComparison.Ordinal))
                    {
                        optimized = true;
                    }
                }
            }
            catch
            {
                Logger.LogErr($"open error: {xisoPath} No such file or directory\n");
                err = 1;
                continue;
            }

            if (rewrite)
            {
                if (optimized)
                {
                    Logger.Log($"{xisoPath} is already optimized, skipping...\n");
                    continue;
                }

                var oldPath = xisoPath + ".old";
                if (File.Exists(oldPath))
                {
                    Logger.LogErr($"{oldPath} already exists, cannot rewrite {xisoPath}\n");
                    continue;
                }

                try
                {
                    File.Move(xisoPath, oldPath);
                    XisoReader.DecodeXiso(oldPath, path, ExtractMode.Rewrite, out var newIsoPath, true);

                    if (err == 0)
                    {
                        Logger.Log($"\n{Logger.TotalFiles} files in {newIsoPath} total {Logger.TotalBytes} bytes\n");
                        Logger.Log($"\n{xisoPath} successfully rewritten{(path != null ? " as " : ".")}{(path != null ? newIsoPath : "")}\n");
                    }

                    if (deleteOld) File.Delete(oldPath);
                }
                catch (Exception ex)
                {
                    Logger.LogErr($"{ex.Message}\n");
                    err = 1;
                }
            }
            else
            {
                try
                {
                    if (extract)
                        XisoReader.Extract(xisoPath, path, !optimized);
                    else
                        XisoReader.List(xisoPath, !optimized);
                }
                catch (ExtractErrorException ex) when (ex.ErrorCode == ExtractError.ErrIsoNoFiles)
                {
                    err = 0;
                }
                catch (Exception ex)
                {
                    Logger.LogErr($"failed to {(extract ? "extract" : "list")} xbox iso image {xisoPath}: {ex.Message}\n");
                    err = 1;
                }
            }

            if (err == 0)
            {
                Logger.Log($"\n{Logger.TotalFiles} files in {xisoPath} total {Logger.TotalBytes} bytes\n");
            }
        }

        if (err == 0 && isos > 1)
            Logger.Log($"\n{Logger.TotalFilesAllIsos} files in {isos} xiso's total {Logger.TotalBytesAllIsos} bytes\n");

        if (Logger.Warned)
            Logger.Log("\nWARNING:  Warning(s) were issued during execution--review stderr!\n");

        return err;
    }

    /// <summary>
    /// Prints the usage/help text to standard error.
    /// </summary>
    private static void PrintUsage()
    {
        Console.Error.Write($"""
                             {Constants.Banner}
                               Usage:

                                 extract-xiso [options] [-[lrx]] <file1.xiso> [file2.xiso] ...
                                 extract-xiso [options] -c <dir> [name] [-c <dir> [name]] ...

                               Mutually exclusive modes:

                                 -c <dir> [name]     Create xiso from file(s) starting in <dir>.
                                 -l                  List files in xiso(s).
                                 -r                  Rewrite xiso(s) as optimized xiso(s).
                                 -x                  Extract xiso(s) (the default mode if none is given).

                               Options:

                                 -d <directory>      In extract mode, expand xiso in <directory>.
                                                     In rewrite mode, rewrite xiso in <directory>.
                                 -D                  In rewrite mode, delete old xiso after processing.
                                 -h                  Print this help text and exit.
                                 -m                  In create or rewrite mode, disable automatic .xbe
                                                       media enable patching (not recommended).
                                 -q                  Run quiet (suppress all non-error output).
                                 -Q                  Run silent (suppress all output).
                                 -s                  Skip $SystemUpdate folder.
                                 -v                  Print version information and exit.

                             """);
    }
}
