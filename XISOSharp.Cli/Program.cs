using System.Globalization;
using System.Security.Cryptography;
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
        var tree = false;
        var info = false;
        var lsMode = false;
        var xexInfoMode = false;
        var unpackMode = false;
        var hashMode = false;
        var copyOut = false;
        var auditMode = false;
        var validateMode = false;
        string? hashAlgo = null;
        var xSeen = false;
        var deleteOld = false;
        string? path = null;
        string? outputName = null;
        var createList = new List<(string Dir, string? Name)>();
        var isos = 0;
        var err = 0;

        var validateFlag = false;
        var validateChecksums = false;
        var validateStrict = false;
        string? validateReport = null;

        int? skipSectors = null;
        int? prependSectors = null;
        var excludePatterns = new List<string>();
        string? batchDir = null;
        var batchRecursive = false;
        string? packInput = null;
        string? packName = null;
        string? packIsoFile = null;

        // XboxKit redump / archival modes
        var videoMode = false;
        var randomMode = false;
        var seedMode = false;
        var wipeMode = false;
        var trimMode = false;
        var petrifyMode = false;
        var updateMode = false;
        var zarMode = false;
        var allMode = false;
        var bestMode = false;
        var compressAlias = false;
        var rebuildMode = false;
        string? securitySectorsPath = null;

        var optind = 0;

        // Handle standalone verb commands early (don't start with '-')
        if (args.Length > 0 && string.Equals(args[0], "validate", StringComparison.OrdinalIgnoreCase))
        {
            validateMode = true;
            extract = false;
            optind = 1;
        }
        else if (args.Length > 0 && string.Equals(args[0], "rebuild", StringComparison.OrdinalIgnoreCase))
        {
            // Rebuild has its own positional+flag parsing (files may appear before -o), handle directly
            return RunRebuildMode(args, 1, null, null);
        }
        else if (args.Length > 0 && string.Equals(args[0], "build-image", StringComparison.OrdinalIgnoreCase))
        {
            return RunBuildImage(args, 1);
        }
        else if (args.Length > 0 && string.Equals(args[0], "image-spec", StringComparison.OrdinalIgnoreCase))
        {
            return RunImageSpec(args, 1);
        }

        for (var i = optind; i < args.Length; i++)
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
                            if (xSeen || rewrite || !extract || i + 1 >= args.Length)
                            {
                                PrintUsage();
                                return 1;
                            }

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
                    case "--unpack":
                        if (xSeen || rewrite || createList.Count > 0)
                        {
                            PrintUsage();
                            return 1;
                        }

                        extract = false;
                        unpackMode = true;
                        break;
                    case "-X":
                        if (i + 1 < args.Length)
                        {
                            excludePatterns.Add(args[++i]);
                        }
                        else
                        {
                            PrintUsage();
                            return 1;
                        }

                        break;
                    case "-l":
                        if (xSeen || rewrite || createList.Count > 0)
                        {
                            PrintUsage();
                            return 1;
                        }

                        extract = false;
                        break;
                    case "-t":
                        if (xSeen || rewrite || createList.Count > 0)
                        {
                            PrintUsage();
                            return 1;
                        }

                        extract = false;
                        tree = true;
                        break;
                    case "-i":
                        if (xSeen || rewrite || createList.Count > 0)
                        {
                            PrintUsage();
                            return 1;
                        }

                        extract = false;
                        info = true;
                        break;
                    case "--ls":
                        if (xSeen || rewrite || createList.Count > 0)
                        {
                            PrintUsage();
                            return 1;
                        }

                        extract = false;
                        lsMode = true;
                        break;
                    case "--xex-info":
                        if (xSeen || rewrite || createList.Count > 0)
                        {
                            PrintUsage();
                            return 1;
                        }

                        extract = false;
                        xexInfoMode = true;
                        break;
                    case "--md5":
                        if (xSeen || rewrite || createList.Count > 0)
                        {
                            PrintUsage();
                            return 1;
                        }

                        extract = false;
                        hashMode = true;
                        hashAlgo = "MD5";
                        break;
                    case "--sha256":
                        if (xSeen || rewrite || createList.Count > 0)
                        {
                            PrintUsage();
                            return 1;
                        }

                        extract = false;
                        hashMode = true;
                        hashAlgo = "SHA256";
                        break;
                    case "-V":
                        if (xSeen || rewrite || createList.Count > 0)
                        {
                            PrintUsage();
                            return 1;
                        }

                        extract = false;
                        auditMode = true;
                        break;
                    case "validate":
                        if (xSeen || rewrite || createList.Count > 0)
                        {
                            PrintUsage();
                            return 1;
                        }

                        extract = false;
                        validateMode = true;
                        break;
                    case "--validate":
                        validateFlag = true;
                        break;
                    case "--validate-checksums":
                        validateFlag = true;
                        validateChecksums = true;
                        break;
                    case "--validate-strict":
                        validateStrict = true;
                        break;
                    case "--validate-report":
                        if (i + 1 < args.Length)
                        {
                            validateReport = args[++i];
                        }
                        else
                        {
                            PrintUsage();
                            return 1;
                        }

                        break;
                    case "--copy-out":
                        if (xSeen || rewrite || createList.Count > 0)
                        {
                            PrintUsage();
                            return 1;
                        }

                        extract = false;
                        copyOut = true;
                        break;
                    case "-r":
                        if (xSeen || !extract || createList.Count > 0)
                        {
                            PrintUsage();
                            return 1;
                        }

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
                        else
                        {
                            PrintUsage();
                            return 1;
                        }

                        break;
                    case "-o":
                        if (i + 1 < args.Length)
                        {
                            outputName = args[++i];
                        }
                        else
                        {
                            PrintUsage();
                            return 1;
                        }

                        break;
                    case "-p":
                        PrintUsage();
                        return 1;
                    case "--skip-sectors":
                        if (i + 1 < args.Length &&
                            int.TryParse(args[i + 1], CultureInfo.InvariantCulture, out var skipVal) && skipVal >= 0)
                        {
                            skipSectors = skipVal;
                            i++;
                        }
                        else
                        {
                            Logger.LogErr(
                                "Error: --skip-sectors requires a non-negative integer (number of 2048-byte sectors)\n");
                            return 1;
                        }

                        break;
                    case "--prepend-sectors":
                        if (i + 1 < args.Length &&
                            int.TryParse(args[i + 1], CultureInfo.InvariantCulture, out var prependVal) &&
                            prependVal >= 0)
                        {
                            prependSectors = prependVal;
                            i++;
                        }
                        else
                        {
                            Logger.LogErr(
                                "Error: --prepend-sectors requires a non-negative integer (number of 2048-byte sectors)\n");
                            return 1;
                        }

                        break;
                    case "--batch":
                        if (i + 1 < args.Length)
                        {
                            batchDir = args[++i];
                        }
                        else
                        {
                            PrintUsage();
                            return 1;
                        }

                        break;
                    case "--batch-recursive":
                        batchRecursive = true;
                        break;
                    case "--pack":
                        if (packInput != null || xSeen || rewrite || createList.Count > 0)
                        {
                            PrintUsage();
                            return 1;
                        }

                        if (i + 1 >= args.Length)
                        {
                            PrintUsage();
                            return 1;
                        }

                        packInput = args[++i];
                        if (i + 1 < args.Length && !args[i + 1].StartsWith('-'))
                        {
                            packName = args[++i];
                        }

                        break;
                    case "--video":
                        if (xSeen || rewrite || createList.Count > 0)
                        {
                            PrintUsage();
                            return 1;
                        }

                        extract = false;
                        videoMode = true;
                        break;
                    case "--random":
                        if (xSeen || rewrite || createList.Count > 0)
                        {
                            PrintUsage();
                            return 1;
                        }

                        extract = false;
                        randomMode = true;
                        break;
                    case "--seed":
                        if (xSeen || rewrite || createList.Count > 0)
                        {
                            PrintUsage();
                            return 1;
                        }

                        extract = false;
                        seedMode = true;
                        break;
                    case "--wipe":
                        if (xSeen || rewrite || createList.Count > 0)
                        {
                            PrintUsage();
                            return 1;
                        }

                        extract = false;
                        wipeMode = true;
                        break;
                    case "--trim":
                        if (xSeen || rewrite || createList.Count > 0)
                        {
                            PrintUsage();
                            return 1;
                        }

                        extract = false;
                        trimMode = true;
                        break;
                    case "--petrify":
                        if (xSeen || rewrite || createList.Count > 0)
                        {
                            PrintUsage();
                            return 1;
                        }

                        extract = false;
                        petrifyMode = true;
                        break;
                    case "--update":
                        if (xSeen || rewrite || createList.Count > 0)
                        {
                            PrintUsage();
                            return 1;
                        }

                        extract = false;
                        updateMode = true;
                        break;
                    case "--zar":
                        if (xSeen || rewrite || createList.Count > 0)
                        {
                            PrintUsage();
                            return 1;
                        }

                        extract = false;
                        zarMode = true;
                        break;
                    case "--all":
                        if (xSeen || rewrite || createList.Count > 0)
                        {
                            PrintUsage();
                            return 1;
                        }

                        extract = false;
                        allMode = true;
                        break;
                    case "--best":
                        if (xSeen || rewrite || createList.Count > 0)
                        {
                            PrintUsage();
                            return 1;
                        }

                        extract = false;
                        bestMode = true;
                        break;
                    case "--compress":
                        if (xSeen || rewrite || createList.Count > 0)
                        {
                            PrintUsage();
                            return 1;
                        }

                        extract = false;
                        compressAlias = true;
                        break;
                    case "--security-sectors":
                    case "--sectors":
                        if (i + 1 < args.Length)
                        {
                            securitySectorsPath = args[++i];
                        }
                        else
                        {
                            PrintUsage();
                            return 1;
                        }

                        break;
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

        // --pack translates to create mode (directory input) or rewrite mode (ISO input),
        // reusing the existing create/rewrite machinery.
        if (TranslatePackInput(packInput, packName, batchDir, rewrite, info, lsMode, xexInfoMode,
                unpackMode, hashMode, copyOut, auditMode, validateMode, tree, extract,
                optind, args.Length, createList, ref rewrite, ref packIsoFile, ref path) != 0)
        {
            return 1;
        }

        if (createList.Count > 0 && skipSectors.HasValue)
        {
            Logger.LogErr("Error: --skip-sectors cannot be combined with -c (create mode)\n");
            return 1;
        }

        if (prependSectors.HasValue && createList.Count == 0 && !rewrite)
        {
            Logger.LogErr("Error: --prepend-sectors requires -c (create) or -r (rewrite) mode\n");
            return 1;
        }

        if ((skipSectors.HasValue || prependSectors.HasValue) &&
            (info || lsMode || xexInfoMode || hashMode || copyOut || auditMode || validateMode || validateFlag))
        {
            Logger.LogErr(
                "Error: --skip-sectors/--prepend-sectors are only supported in extract, list, tree, rewrite (-r), unpack, and create (-c) modes\n");
            return 1;
        }

        if (excludePatterns.Count > 0 && createList.Count == 0)
        {
            Logger.LogErr("Error: -X (exclude pattern) requires -c (create) mode\n");
            return 1;
        }

        if (batchRecursive && batchDir == null)
        {
            Logger.LogErr("Error: --batch-recursive requires --batch <directory>\n");
            return 1;
        }

        if (batchDir != null && (createList.Count > 0 || info || lsMode || xexInfoMode || unpackMode || hashMode ||
                                 copyOut || validateMode))
        {
            Logger.LogErr(
                "Error: --batch is only supported in extract, list, tree, rewrite (-r), and audit (-V) modes\n");
            return 1;
        }

        if (unpackMode && (info || lsMode || xexInfoMode || tree || hashMode || copyOut || auditMode || validateMode))
        {
            Logger.LogErr("Error: --unpack cannot be combined with other modes\n");
            return 1;
        }

        // XboxKit redump modes are mutually exclusive with other operational modes
        var anyRedumpMode = videoMode || randomMode || seedMode || wipeMode || trimMode || petrifyMode || updateMode ||
                            zarMode || allMode || bestMode || compressAlias || rebuildMode;
        if (anyRedumpMode && (info || lsMode || xexInfoMode || tree || hashMode || copyOut || auditMode ||
                              validateMode || unpackMode || createList.Count > 0 || rewrite))
        {
            Logger.LogErr(
                "Error: --video/--random/--seed/--wipe/--trim/--petrify/--update/--zar/--all/--best/--compress/rebuild cannot be combined with other modes\n");
            return 1;
        }

        if ((anyRedumpMode || rebuildMode) && batchDir != null)
        {
            Logger.LogErr("Error: --batch cannot be combined with redump modes\n");
            return 1;
        }

        // Expand --all / --best / --compress aliases into individual flags
        if (allMode)
        {
            randomMode = true;
            seedMode = true;
            trimMode = true;
            updateMode = true;
            videoMode = true;
            wipeMode = true;
        }

        if (bestMode)
        {
            trimMode = true;
            wipeMode = true;
        }

        if (compressAlias)
        {
            petrifyMode = true;
            updateMode = true;
            videoMode = true;
            zarMode = true;
        }

        // The list of ISO files to process: explicit filenames, a --batch directory scan,
        // or a --pack ISO input.
        var isoFiles = ExpandIsoFiles(batchDir, batchRecursive, args, optind, packIsoFile);
        if (isoFiles == null)
        {
            return 1;
        }

        if (createList.Count > 0)
        {
            if (optind < args.Length)
            {
                PrintUsage();
                return 1;
            }
        }
        else if (isoFiles.Count == 0)
        {
            PrintUsage();
            return 1;
        }

        Logger.Log(Constants.Banner);

        // Dispatch XboxKit redump modes (batch) — after expansion so --batch handling is uniform,
        // but rebuild already returned above.
        if (videoMode || randomMode || seedMode || wipeMode || trimMode || petrifyMode || updateMode || zarMode)
        {
            return RunRedumpBatch(isoFiles, videoMode, randomMode, seedMode, wipeMode, trimMode, petrifyMode,
                updateMode, zarMode, securitySectorsPath, outputName);
        }

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

                try
                {
                    // Allow the output name to include a not-yet-existing directory.
                    if (outputDir != null)
                    {
                        Directory.CreateDirectory(outputDir);
                    }

                    XisoWriter.CreateXiso(dir, outputDir, null, null, out _, isoName, null,
                        prependSectors: prependSectors,
                        excludePatterns: excludePatterns.Count > 0 ? excludePatterns : null);
                }
                catch (UnauthorizedAccessException ex)
                {
                    Logger.LogErr($"Error: permission denied: {ex.Message}\n");
                    return 1;
                }
                catch (IOException ex)
                {
                    Logger.LogErr($"Error: {ex.Message}\n");
                    return 1;
                }
                catch (Exception ex)
                {
                    Logger.LogErr($"Error: {ex.Message}\n");
                    return 1;
                }
            }

            return 0;
        }

        if (info)
        {
            if (optind >= args.Length)
            {
                PrintUsage();
                return 1;
            }

            var xisoPath = args[optind];
            var internalPath = optind + 1 < args.Length ? args[optind + 1] : "/";

            try
            {
                var volInfo = XisoReader.GetVolumeInfo(xisoPath);

                if (!volInfo.IsValid)
                {
                    Logger.LogErr($"{xisoPath} does not appear to be a valid xbox iso image\n");
                    return 1;
                }

                Logger.Log($"Volume: {xisoPath}\n");
                Logger.Log($"  Valid:          {volInfo.IsValid}\n");
                Logger.Log($"  File Length:    {volInfo.FileLength} bytes ({volInfo.FileLength / 1024 / 1024} MB)\n");
                Logger.Log($"  Total Sectors:  {volInfo.TotalSectors}\n");
                Logger.Log($"  Disc Offset:    0x{volInfo.DiscLseek:X8}\n");
                Logger.Log($"  Root Sector:    {volInfo.RootDirSector}\n");
                Logger.Log($"  Root Size:      {volInfo.RootDirSize} bytes\n");
                Logger.Log("\n");

                var entries = XisoReader.ListDirectory(xisoPath, internalPath);
                if (entries.Count == 0)
                {
                    Logger.Log($"{internalPath}: empty directory\n");
                }
                else
                {
                    Logger.Log($"Directory: {internalPath}\n\n");
                    foreach (var entry in entries)
                    {
                        Logger.Log($"  {entry.Name}{(entry.IsDirectory ? "/" : "")}\n");
                        Logger.Log($"    Sector:    {entry.StartSector}\n");
                        Logger.Log($"    Size:      {entry.FileSize} bytes\n");
                        Logger.Log($"    Attrs:     0x{entry.Attributes:X2}{FormatAttributes(entry.Attributes)}\n");
                        Logger.Log(
                            $"    L-Offset:  {(entry.LeftChildOffset == 0 ? "none" : entry.LeftChildOffset.ToString())}\n");
                        Logger.Log(
                            $"    R-Offset:  {(entry.RightChildOffset == 0 ? "none" : entry.RightChildOffset.ToString())}\n");
                        Logger.Log("\n");
                    }
                }
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException)
            {
                Logger.LogErr($"Error: {ex.Message}\n");
                return 1;
            }

            return 0;
        }

        if (lsMode)
        {
            if (optind >= args.Length)
            {
                PrintUsage();
                return 1;
            }

            var xisoPath = args[optind];
            var internalPath = optind + 1 < args.Length ? args[optind + 1] : "/";

            try
            {
                var entries = XisoReader.ListDirectoryFlat(xisoPath, internalPath);
                if (entries.Count == 0)
                {
                    Logger.Log($"{internalPath}: empty directory\n");
                }
                else
                {
                    foreach (var name in entries)
                    {
                        Logger.Log($"{name}\n");
                    }
                }
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException)
            {
                Logger.LogErr($"Error: {ex.Message}\n");
                return 1;
            }

            return 0;
        }

        if (xexInfoMode)
        {
            if (optind + 1 >= args.Length)
            {
                PrintUsage();
                return 1;
            }

            var xisoPath = args[optind];
            var internalPath = args[optind + 1];

            try
            {
                var xex = XisoReader.GetXexInfo(xisoPath, internalPath);
                if (xex == null)
                {
                    Logger.LogErr($"Not an XEX2 executable: {internalPath}\n");
                    return 1;
                }

                Logger.Log($"XEX info: {internalPath}\n");
                Logger.Log($"  Module flags:      0x{xex.ModuleFlags:X8}{FormatXexModuleFlags(xex.ModuleFlags)}\n");
                Logger.Log($"  Header size:       0x{xex.HeaderSize:X8}\n");
                Logger.Log($"  Entry point:       0x{xex.EntryPoint:X8}\n");
                Logger.Log($"  Image base:        0x{xex.ImageBaseAddress:X8}\n");
                Logger.Log($"  Image size:        0x{xex.ImageSize:X8}\n");
                Logger.Log($"  Load address:      0x{xex.LoadAddress:X8}\n");
                Logger.Log($"  Region:            0x{xex.Region:X8}{FormatXexRegion(xex.Region)}\n");
                Logger.Log(
                    $"  Media types:       0x{xex.AllowedMediaTypes:X8}{FormatXexMediaTypes(xex.AllowedMediaTypes)}\n");
                Logger.Log($"  Media ID:          0x{xex.MediaId:X8}\n");
                Logger.Log($"  Title ID:          0x{xex.TitleId:X8}\n");
                Logger.Log($"  Version:           0x{xex.Version:X8}\n");
                Logger.Log($"  Platform:          0x{xex.Platform:X2}\n");
                Logger.Log($"  Disc:              {xex.DiscNumber}/{xex.DiscCount}\n");
                Logger.Log($"  Encryption:        {xex.EncryptionType} ({FormatXexEncryption(xex.EncryptionType)})\n");
                Logger.Log(
                    $"  Compression:       {xex.CompressionType} ({FormatXexCompression(xex.CompressionType)})\n");
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException)
            {
                Logger.LogErr($"Error: {ex.Message}\n");
                return 1;
            }

            return 0;
        }

        if (unpackMode)
        {
            return RunUnpackMode(args, optind, path, skipSectors);
        }

        if (hashMode)
        {
            if (optind >= args.Length)
            {
                PrintUsage();
                return 1;
            }

            var xisoPath = args[optind];
            var internalPath = optind + 1 < args.Length ? args[optind + 1] : null;
            var algorithm = new HashAlgorithmName(hashAlgo!);

            try
            {
                if (internalPath != null)
                {
                    // Hash specific file or all files in a directory
                    var entry = XisoReader.GetEntryInfo(xisoPath, internalPath);
                    if (entry == null)
                    {
                        Logger.LogErr($"Path not found: {internalPath}\n");
                        return 1;
                    }

                    if (entry.IsDirectory)
                    {
                        var results = XisoReader.ComputeDirectoryHashes(xisoPath, internalPath, algorithm);
                        foreach ((string filePath, byte[] hash) in results)
                        {
                            Logger.Log($"{Convert.ToHexString(hash).ToLowerInvariant()}  {filePath}\n");
                        }
                    }
                    else
                    {
                        var hash = XisoReader.ComputeFileHash(xisoPath, internalPath, algorithm);
                        if (hash != null)
                            Logger.Log($"{Convert.ToHexString(hash).ToLowerInvariant()}  {internalPath}\n");
                    }
                }
                else
                {
                    // Hash all files
                    var results = XisoReader.ComputeDirectoryHashes(xisoPath, "/", algorithm);
                    foreach ((string filePath, byte[] hash) in results)
                    {
                        Logger.Log($"{Convert.ToHexString(hash).ToLowerInvariant()}  {filePath}\n");
                    }
                }
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException)
            {
                Logger.LogErr($"Error: {ex.Message}\n");
                return 1;
            }

            return 0;
        }

        if (copyOut)
        {
            if (optind + 2 >= args.Length)
            {
                PrintUsage();
                return 1;
            }

            var xisoPath = args[optind];
            var internalPath = args[optind + 1];
            var destPath = args[optind + 2];

            try
            {
                var entry = XisoReader.GetEntryInfo(xisoPath, internalPath);
                if (entry == null)
                {
                    Logger.LogErr($"Path not found in XISO: {internalPath}\n");
                    return 1;
                }

                XisoReader.CopyOut(xisoPath, internalPath, destPath);
                Logger.Log($"Copied {internalPath} to {destPath}\n");
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
            {
                Logger.LogErr($"Error: {ex.Message}\n");
                return 1;
            }

            return 0;
        }

        if (auditMode)
        {
            if (isoFiles.Count == 0)
            {
                PrintUsage();
                return 1;
            }

            var allValid = true;
            for (var i = 0; i < isoFiles.Count; i++)
            {
                var xisoPath = isoFiles[i];
                try
                {
                    var result = XisoReader.AuditXiso(xisoPath);
                    Logger.Log($"Auditing {xisoPath}:\n\n");
                    Logger.Log($"  Files checked:  {result.FilesChecked}\n");
                    Logger.Log($"  Dirs checked:   {result.DirsChecked}\n");

                    if (result.Issues.Count == 0)
                    {
                        Logger.Log("  Result:         PASS\n");
                    }
                    else
                    {
                        allValid = false;
                        Logger.Log($"  Result:         FAIL ({result.Issues.Count} issue(s))\n");
                        foreach (var issue in result.Issues)
                        {
                            Logger.LogErr($"    - {issue}\n");
                        }
                    }

                    Logger.Log("\n");
                }
                catch (Exception ex) when (ex is InvalidDataException or IOException)
                {
                    Logger.LogErr($"Error auditing {xisoPath}: {ex.Message}\n");
                    allValid = false;
                }
            }

            return allValid ? 0 : 1;
        }

        if (validateMode)
        {
            if (optind + 1 >= args.Length)
            {
                PrintUsage();
                return 1;
            }

            var sourcePath = args[optind];
            var outputPath = args[optind + 1];

            try
            {
                var result = XisoValidator.ValidateConversion(sourcePath, outputPath, validateChecksums);
                XisoValidator.LogResult(result, sourcePath, outputPath);

                if (validateReport != null)
                {
                    XisoValidator.WriteReport(result, sourcePath, outputPath, validateReport);
                    Logger.Log($"[VALIDATE] Report written to {validateReport}\n");
                }

                return result.Passed ? 0 : 2;
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException or XisoFormatException)
            {
                Logger.LogErr($"Error validating: {ex.Message}\n");
                return 1;
            }
        }

        for (var i = 0; i < isoFiles.Count; i++)
        {
            isos++;
            Logger.Log("\n");
            Logger.TotalBytes = Logger.TotalFiles = 0;

            var xisoPath = isoFiles[i];
            var optimized = false;

            try
            {
                using var tagFs = new FileStream(xisoPath,
                    new FileStreamOptions
                    {
                        Mode = FileMode.Open, Access = FileAccess.Read, Share = FileShare.Read, BufferSize = 256
                    });

                tagFs.Seek(Constants.OptimizedTagOffset, SeekOrigin.Begin);
                var tagBuf = new byte[Constants.OptimizedTagLength];
                var tagRead = tagFs.Read(tagBuf);
                if (tagRead == Constants.OptimizedTagLength)
                {
                    var tag = Encoding.ASCII.GetString(tagBuf);
                    if (tag.StartsWith(Constants.OptimizedTag[..Constants.OptimizedTagLengthMin],
                            StringComparison.Ordinal))
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
                    XisoReader.DecodeXiso(oldPath, path, ExtractMode.Rewrite, out var newIsoPath, true,
                        outputName: outputName, skipSectors: skipSectors, prependSectors: prependSectors);

                    if (err == 0)
                    {
                        Logger.Log($"\n{Logger.TotalFiles} files in {newIsoPath} total {Logger.TotalBytes} bytes\n");
                        Logger.Log(
                            $"\n{xisoPath} successfully rewritten{(path != null ? " as " : ".")}{(path != null ? newIsoPath : "")}\n");
                    }

                    if (err == 0 && validateFlag && newIsoPath != null)
                    {
                        Logger.Log("\n");
                        var valResult = XisoValidator.ValidateConversion(oldPath, newIsoPath, validateChecksums);
                        XisoValidator.LogResult(valResult, oldPath, newIsoPath);

                        if (validateReport != null)
                        {
                            XisoValidator.WriteReport(valResult, oldPath, newIsoPath, validateReport);
                            Logger.Log($"[VALIDATE] Report written to {validateReport}\n");
                        }

                        if (!valResult.Passed && validateStrict)
                        {
                            err = 2;
                        }
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
                        XisoReader.Extract(xisoPath, path, !optimized, skipSectors: skipSectors);
                    else if (tree)
                        XisoReader.Tree(xisoPath, !optimized, skipSectors: skipSectors);
                    else
                        XisoReader.List(xisoPath, !optimized, skipSectors: skipSectors);
                }
                catch (ExtractErrorException ex) when (ex.ErrorCode == ExtractError.ErrIsoNoFiles)
                {
                    err = 0;
                }
                catch (UnauthorizedAccessException ex)
                {
                    Logger.LogErr($"Error: permission denied: {ex.Message}\n");
                    err = 1;
                }
                catch (IOException ex)
                {
                    Logger.LogErr($"Error: {ex.Message}\n");
                    err = 1;
                }
                catch (Exception ex)
                {
                    Logger.LogErr(
                        $"failed to {(extract ? "extract" : tree ? "tree" : "list")} xbox iso image {xisoPath}: {ex.Message}\n");
                    err = 1;
                }
            }

            if (err == 0)
            {
                if (tree)
                    Logger.Log($"{Logger.TotalFiles} files, {Logger.TotalBytes} bytes\n");
                else
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
    /// Executes the <c>--unpack</c> mode: unpack one ISO to a destination directory
    /// (defaulting to the ISO name). Returns the process exit code.
    /// </summary>
    private static int RunUnpackMode(string[] args, int optind, string? path, int? skipSectors)
    {
        if (optind >= args.Length)
        {
            PrintUsage();
            return 1;
        }

        if (path != null)
        {
            Logger.LogErr("Error: --unpack takes the destination as an argument; -d is not used with --unpack\n");
            return 1;
        }

        var xisoPath = args[optind];
        var destPath = optind + 1 < args.Length ? args[optind + 1] : null;

        try
        {
            var result = XisoReader.UnpackImage(xisoPath, destPath, skipSectors: skipSectors);
            return result == 0 ? 0 : 1;
        }
        catch (ExtractErrorException ex) when (ex.ErrorCode == ExtractError.ErrIsoNoFiles)
        {
            return 0;
        }
        catch (UnauthorizedAccessException ex)
        {
            Logger.LogErr($"Error: permission denied: {ex.Message}\n");
            return 1;
        }
        catch (IOException ex)
        {
            Logger.LogErr($"Error: {ex.Message}\n");
            return 1;
        }
        catch (Exception ex)
        {
            Logger.LogErr($"failed to unpack xbox iso image {xisoPath}: {ex.Message}\n");
            return 1;
        }
    }

    private static int RunRebuildMode(string[] args, int optind, string? outputName, string? securitySectorsPath)
    {
        string? outRebuild = outputName;
        string? secPath = securitySectorsPath;
        var positionals = new List<string>();
        for (int i = optind; i < args.Length; i++)
        {
            var a = args[i];
            if (string.Equals(a, "-o", StringComparison.OrdinalIgnoreCase) || string.Equals(a, "--output", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 >= args.Length)
                {
                    Logger.LogErr("Error: -o requires a filename\n");
                    return 1;
                }

                outRebuild = args[++i];
            }
            else if (string.Equals(a, "--security-sectors", StringComparison.OrdinalIgnoreCase) || string.Equals(a, "--sectors", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 >= args.Length)
                {
                    PrintUsage();
                    return 1;
                }

                secPath = args[++i];
            }
            else if (string.Equals(a, "-q", StringComparison.OrdinalIgnoreCase))
            {
                Logger.Quiet = true;
            }
            else if (string.Equals(a, "-Q", StringComparison.OrdinalIgnoreCase))
            {
                Logger.Quiet = Logger.RealQuiet = true;
            }
            else if (string.Equals(a, "-h", StringComparison.OrdinalIgnoreCase) || string.Equals(a, "--help", StringComparison.OrdinalIgnoreCase))
            {
                PrintUsage();
                return 0;
            }
            else if (string.Equals(a, "-v", StringComparison.OrdinalIgnoreCase) || string.Equals(a, "--version", StringComparison.OrdinalIgnoreCase))
            {
                Console.Write(Constants.Banner);
                return 0;
            }
            else if (a.StartsWith('-'))
            {
                Logger.LogErr($"Error: unknown option for rebuild: {a}\n");
                PrintUsage();
                return 1;
            }
            else
            {
                positionals.Add(a);
            }
        }

        if (positionals.Count == 0)
        {
            Logger.LogErr("Error: rebuild requires at least <xiso> [video.iso] [filler|seed] [su...]\n");
            PrintUsage();
            return 1;
        }

        string xisoPath = positionals[0];
        string? videoPath = positionals.Count > 1 ? positionals[1] : null;
        string? fillerOrSeed = positionals.Count > 2 ? positionals[2] : null;
        string? updatePath = positionals.Count > 3 ? positionals[3] : null;

        if (positionals.Count > 4)
        {
            Logger.LogErr(
                "Error: rebuild takes at most 4 positional files: <xiso> [video.iso] [filler|seed] [update]\n");
            return 1;
        }

        // If video path not provided, try to derive from xiso directory
        if (videoPath == null)
        {
            string dir = Path.GetDirectoryName(xisoPath) ?? ".";
            string baseName = Path.GetFileNameWithoutExtension(xisoPath);
            // Strip compound .xiso etc.
            if (baseName.EndsWith(".xiso", StringComparison.OrdinalIgnoreCase)) baseName = baseName[..^5];
            string candidate = Path.Combine(dir, baseName + ".video.iso");
            if (File.Exists(candidate))
            {
                videoPath = candidate;
            }
            else
            {
                // Also try sibling redump video naming
                candidate = Path.Combine(dir, baseName + ".video.iso");
                // If still not found, keep null and let Rebuild fail with clear message
                videoPath = candidate;
            }
        }

        string outRedump = outRebuild ?? DeriveRedumpPath(xisoPath);

        try
        {
            bool ok = XisoRedump.RebuildRedump(xisoPath, videoPath, fillerOrSeed, updatePath, outRedump, secPath,
                quiet: Logger.Quiet);
            if (!ok)
            {
                Logger.LogErr($"[ERROR] Rebuild failed for {xisoPath}\n");
                return 1;
            }

            Logger.Log($"Rebuilt {outRedump} ({new FileInfo(outRedump).Length} bytes)\n");
            return 0;
        }
        catch (Exception ex)
        {
            Logger.LogErr($"Error rebuilding: {ex.Message}\n");
            return 1;
        }
    }

    private static int RunBuildImage(string[] args, int optind)
    {
        string? specFile = null;
        var mapRaw = new List<string>();
        string? metaOutput = null;
        bool dryRun = false;
        var positionals = new List<string>();

        for (int i = optind; i < args.Length; i++)
        {
            var a = args[i];
            if (string.Equals(a, "-f", StringComparison.OrdinalIgnoreCase) || string.Equals(a, "--file", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 >= args.Length)
                {
                    Logger.LogErr("Error: -f requires a filename\n");
                    return 1;
                }

                specFile = args[++i];
            }
            else if (string.Equals(a, "-m", StringComparison.OrdinalIgnoreCase) || string.Equals(a, "--map", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 >= args.Length)
                {
                    Logger.LogErr("Error: -m requires a rule\n");
                    return 1;
                }

                mapRaw.Add(args[++i]);
            }
            else if (string.Equals(a, "-O", StringComparison.OrdinalIgnoreCase) || string.Equals(a, "--output", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 >= args.Length)
                {
                    Logger.LogErr("Error: -O requires a path\n");
                    return 1;
                }

                metaOutput = args[++i];
            }
            else if (string.Equals(a, "-D", StringComparison.OrdinalIgnoreCase) || string.Equals(a, "--dry-run", StringComparison.OrdinalIgnoreCase) || string.Equals(a, "--dryrun", StringComparison.OrdinalIgnoreCase))
            {
                dryRun = true;
            }
            else if (string.Equals(a, "-h", StringComparison.OrdinalIgnoreCase) || string.Equals(a, "--help", StringComparison.OrdinalIgnoreCase))
            {
                PrintUsage();
                return 0;
            }
            else if (string.Equals(a, "-v", StringComparison.OrdinalIgnoreCase) || string.Equals(a, "--version", StringComparison.OrdinalIgnoreCase))
            {
                Console.Write(Constants.Banner);
                return 0;
            }
            else if (string.Equals(a, "-q", StringComparison.OrdinalIgnoreCase))
            {
                Logger.Quiet = true;
            }
            else if (string.Equals(a, "-Q", StringComparison.OrdinalIgnoreCase))
            {
                Logger.Quiet = Logger.RealQuiet = true;
            }
            else if (a.StartsWith('-'))
            {
                Logger.LogErr($"Error: unknown option for build-image: {a}\n");
                PrintUsage();
                return 1;
            }
            else
            {
                positionals.Add(a);
            }
        }

        if (specFile != null && mapRaw.Count > 0)
        {
            Logger.LogErr("Error: --file and --map are mutually exclusive\n");
            return 1;
        }

        if (specFile != null && metaOutput != null)
        {
            Logger.LogErr("Error: --file and --output are mutually exclusive (use output inside spec file)\n");
            return 1;
        }

        if (positionals.Count > 2)
        {
            Logger.LogErr("Error: build-image takes at most 2 positional arguments: [sourceDir] [output.iso]\n");
            return 1;
        }

        string sourcePathStr = positionals.Count >= 1 ? positionals[0] : Directory.GetCurrentDirectory();
        string? imagePathStr = positionals.Count >= 2 ? positionals[1] : null;

        // Resolve sourceDir and specPath candidate
        string sourceDir;
        string specPath;
        {
            bool isDir = false;
            bool isFile = false;
            try
            {
                isDir = Directory.Exists(sourcePathStr) &&
                        (File.GetAttributes(sourcePathStr) & FileAttributes.Directory) != 0;
            }
            catch
            {
                // ignored
            }

            try { isFile = File.Exists(sourcePathStr); }
            catch
            {
                // ignored
            }

            if (isDir)
            {
                sourceDir = Path.GetFullPath(sourcePathStr);
                specPath = Path.Combine(sourceDir, "xdvdfs.toml");
            }
            else if (isFile)
            {
                specPath = Path.GetFullPath(sourcePathStr);
                sourceDir = Path.GetDirectoryName(specPath) ?? Directory.GetCurrentDirectory();
            }
            else
            {
                // Non-existent path – treat as directory to be created / must exist
                sourceDir = Path.GetFullPath(sourcePathStr);
                specPath = Path.Combine(sourceDir, "xdvdfs.toml");
            }

            if (specFile != null)
                specPath = Path.GetFullPath(specFile);
        }

        var rules = new List<RemapRule>();
        string? specOutput = null;

        if (mapRaw.Count > 0)
        {
            foreach (var raw in mapRaw)
            {
                if (!RemapRule.TryParse(raw, out var r, out var err))
                {
                    Logger.LogErr($"Error: invalid map rule \"{raw}\": {err}\n");
                    return 1;
                }

                rules.Add(r!);
            }

            specOutput = metaOutput;
        }
        else
        {
            if (File.Exists(specPath))
            {
                try
                {
                    (string? outp, List<RemapRule> parsed) = RemapFilesystem.ParseSpecFile(specPath);
                    specOutput = outp;
                    rules.AddRange(parsed);
                }
                catch (Exception ex)
                {
                    Logger.LogErr($"Error reading spec file {specPath}: {ex.Message}\n");
                    return 1;
                }
            }
        }

        if (rules.Count == 0)
        {
            Logger.LogErr("Must specify at least one map rule (via -m or xdvdfs.toml)\n");
            PrintUsage();
            return 1;
        }

        string outputIso;
        if (imagePathStr != null)
        {
            outputIso = Path.GetFullPath(imagePathStr);
        }
        else if (specOutput != null)
        {
            outputIso = Path.IsPathRooted(specOutput) ? specOutput : Path.Combine(sourceDir, specOutput);
            outputIso = Path.GetFullPath(outputIso);
        }
        else if (metaOutput != null && mapRaw.Count > 0)
        {
            outputIso = Path.IsPathRooted(metaOutput) ? metaOutput : Path.Combine(sourceDir, metaOutput);
            outputIso = Path.GetFullPath(outputIso);
        }
        else
        {
            // Default: <sourceDir>.xiso.iso sibling of sourceDir
            var trimmed = sourceDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var parent = Path.GetDirectoryName(trimmed) ?? Directory.GetCurrentDirectory();
            var name = Path.GetFileName(trimmed);
            if (string.IsNullOrEmpty(name)) name = "image";
            // with_extension is_dir=true logic: append .xiso.iso
            if (Path.HasExtension(name))
                outputIso = Path.Combine(parent, name + ".xiso.iso");
            else
                outputIso = Path.Combine(parent, name + ".xiso.iso");
            outputIso = Path.GetFullPath(outputIso);
        }

        if (dryRun)
        {
            try
            {
                var list = RemapFilesystem.DryRunRemap(sourceDir, rules);
                foreach ((string host, string guest) in list)
                    Console.WriteLine($"{host} -> {guest}");
                return 0;
            }
            catch (Exception ex)
            {
                Logger.LogErr($"Dry-run failed: {ex.Message}\n");
                return 1;
            }
        }
        else
        {
            if (!Directory.Exists(sourceDir))
            {
                Logger.LogErr($"Source directory not found: {sourceDir}\n");
                return 1;
            }

            // Ensure output directory exists
            var outDir = Path.GetDirectoryName(outputIso);
            if (!string.IsNullOrEmpty(outDir))
                Directory.CreateDirectory(outDir);
            Logger.Log($"{Constants.Banner}");
            return RemapFilesystem.BuildImage(sourceDir, outputIso, rules);
        }
    }

    private static int RunImageSpec(string[] args, int optind)
    {
        if (optind >= args.Length || !string.Equals(args[optind], "from", StringComparison.OrdinalIgnoreCase))
        {
            Logger.LogErr("Error: image-spec requires 'from' subcommand\n");
            PrintUsage();
            return 1;
        }

        optind++;
        var mapRaw = new List<string>();
        string? metaOutput = null;
        var positionals = new List<string>();
        for (int i = optind; i < args.Length; i++)
        {
            var a = args[i];
            if (string.Equals(a, "-m", StringComparison.OrdinalIgnoreCase) || string.Equals(a, "--map", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 >= args.Length)
                {
                    Logger.LogErr("Error: -m requires a rule\n");
                    return 1;
                }

                mapRaw.Add(args[++i]);
            }
            else if (string.Equals(a, "-O", StringComparison.OrdinalIgnoreCase) || string.Equals(a, "--output", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 >= args.Length)
                {
                    Logger.LogErr("Error: -O requires a path\n");
                    return 1;
                }

                metaOutput = args[++i];
            }
            else if (string.Equals(a, "-h", StringComparison.OrdinalIgnoreCase) || string.Equals(a, "--help", StringComparison.OrdinalIgnoreCase))
            {
                PrintUsage();
                return 0;
            }
            else if (string.Equals(a, "-v", StringComparison.OrdinalIgnoreCase) || string.Equals(a, "--version", StringComparison.OrdinalIgnoreCase))
            {
                Console.Write(Constants.Banner);
                return 0;
            }
            else if (a.StartsWith('-'))
            {
                Logger.LogErr($"Error: unknown option for image-spec from: {a}\n");
                PrintUsage();
                return 1;
            }
            else
            {
                positionals.Add(a);
            }
        }

        if (positionals.Count > 1)
        {
            Logger.LogErr("Error: image-spec from takes at most one output file\n");
            return 1;
        }

        var rules = new List<RemapRule>();
        foreach (var raw in mapRaw)
        {
            if (!RemapRule.TryParse(raw, out var r, out var err))
            {
                Logger.LogErr($"Error: invalid map rule \"{raw}\": {err}\n");
                return 1;
            }

            rules.Add(r!);
        }

        string? outFile = positionals.Count == 1 ? positionals[0] : null;
        var toml = RemapFilesystem.GenerateSpecText(rules, metaOutput);
        if (outFile != null)
        {
            try
            {
                File.WriteAllText(outFile, toml, Encoding.UTF8);
                Logger.Log($"Wrote {outFile}\n");
            }
            catch (Exception ex)
            {
                Logger.LogErr($"Failed to write {outFile}: {ex.Message}\n");
                return 1;
            }
        }
        else
        {
            Console.Write(toml);
        }

        return 0;
    }

    private static string DeriveRedumpPath(string xisoPath)
    {
        string dir = Path.GetDirectoryName(xisoPath) ?? "";
        string full = Path.GetFileName(xisoPath) ?? "redump";
        string baseName = full;
        if (full.EndsWith(".xiso", StringComparison.OrdinalIgnoreCase)) baseName = full[..^5];
        else if (full.EndsWith(".iso", StringComparison.OrdinalIgnoreCase)) baseName = full[..^4];
        return Path.Combine(dir, baseName + ".redump.iso");
    }

    private static int RunRedumpBatch(List<string> isoFiles, bool video, bool random, bool seed, bool wipe, bool trim,
        bool petrify, bool update, bool zar, string? securitySectorsPath, string? outputName)
    {
        // Single-output guard
        bool singleModeCount = new[] { video, random, seed, wipe, trim, petrify, update, zar }.Count(b => b) == 1;
        if (outputName != null && isoFiles.Count != 1 && singleModeCount)
        {
            Logger.LogErr("Error: -o <output> can only be used with a single input file\n");
            return 1;
        }

        int exit = 0;
        foreach (var iso in isoFiles)
        {
            long size = 0;
            try { size = new FileInfo(iso).Length; }
            catch
            {
                Logger.LogErr($"Cannot stat {iso}\n");
                exit = 1;
                continue;
            }

            int redumpType = XgdTables.GetRedumpIsoTypeBySize(size);
            int xisoType = XgdTables.GetXisoTypeBySize(size);
            int vidType = XgdTables.GetVideoTypeBySize(size);
            bool isRedump = redumpType >= 0;
            bool isXiso = xisoType >= 0;
            bool isVideo = vidType >= 0;

            // Derive isoOffset / length for partition ops
            long isoOffset = 0;
            long xisoLen = size;
            if (isRedump)
            {
                int videoTypeForRedump = -1;
                // Need to get videoType via PVD for wave-dependent sizes — open file
                try
                {
                    using var fs = new FileStream(iso, FileMode.Open, FileAccess.Read, FileShare.Read, 65536);
                    videoTypeForRedump = XgdTables.GetVideoType(fs, redumpType);
                }
                catch
                {
                    // ignored
                }

                int vType = videoTypeForRedump >= 0 ? videoTypeForRedump : 0;
                int xsType = XgdTables.GetXisoTypeFromVideo(vType >= 0 ? vType : 0);
                if (xsType < 0 || xsType >= XgdTables.XisoOffset.Length)
                    xsType = redumpType >= 0 ? XgdTables.GetXgdType(redumpType) : 0;
                isoOffset = XgdTables.XisoOffset[xsType];
                xisoLen = XgdTables.XisoLength[xsType];
            }

            string dir = Path.GetDirectoryName(iso) ?? "";
            string full = Path.GetFileName(iso) ?? "output";
            string baseName = full;
            if (full.EndsWith(".redump.iso", StringComparison.OrdinalIgnoreCase))
                baseName = full[..^".redump.iso".Length];
            else if (full.EndsWith(".video.iso", StringComparison.OrdinalIgnoreCase))
                baseName = full[..^".video.iso".Length];
            else if (full.EndsWith(".iso", StringComparison.OrdinalIgnoreCase)) baseName = full[..^".iso".Length];
            else if (full.EndsWith(".xiso", StringComparison.OrdinalIgnoreCase)) baseName = full[..^".xiso".Length];

            if (video)
            {
                if (!isRedump)
                {
                    if (singleModeCount)
                    {
                        Logger.LogErr($"[ERROR] --video requires a Redump ISO (got {iso} size {size})\n");
                        exit = 1;
                    }
                    else
                    {
                        Logger.Log($"[INFO] Skipping --video for non-Redump {iso}\n");
                    }
                }
                else
                {
                    string outVideo = (outputName != null && singleModeCount)
                        ? outputName
                        : Path.Combine(dir, baseName + ".video.iso");
                    if (!XisoRedump.TryExtractVideo(iso, outVideo, out var outPath, Logger.Quiet))
                    {
                        Logger.LogErr($"[ERROR] Failed extracting video from {iso}\n");
                        exit = 1;
                    }
                    else
                    {
                        Logger.Log($"Video extracted to {outPath}\n");
                    }
                }
            }

            if (update)
            {
                if (isVideo)
                {
                    string outUpd = (outputName != null && singleModeCount)
                        ? outputName
                        : Path.Combine(dir, "su20076000_00000000");
                    if (!XisoRedump.TryExtractUpdate(iso, outUpd, wipe: true, quiet: Logger.Quiet))
                    {
                        Logger.LogErr($"[ERROR] Failed extracting update from video {iso}\n");
                        exit = 1;
                    }
                    else
                    {
                        Logger.Log($"Update extracted to {outUpd}\n");
                    }
                }
                else if (isRedump)
                {
                    // Need video file — if video was just extracted, it will exist at derived path
                    string videoPath = Path.Combine(dir, baseName + ".video.iso");
                    if (!File.Exists(videoPath))
                    {
                        if (singleModeCount)
                        {
                            Logger.LogErr(
                                $"[ERROR] --update for Redump requires video partition {videoPath} (run --video first)\n");
                            exit = 1;
                        }
                        else
                        {
                            Logger.Log($"[INFO] Skipping --update (video {videoPath} not found) for {iso}\n");
                        }
                    }
                    else
                    {
                        string outUpd = (outputName != null && singleModeCount)
                            ? outputName
                            : Path.Combine(dir, "su20076000_00000000");
                        if (!XisoRedump.TryExtractUpdate(videoPath, outUpd, wipe: true, quiet: Logger.Quiet))
                        {
                            Logger.LogErr($"[ERROR] Failed extracting update from {videoPath}\n");
                            exit = 1;
                        }
                        else
                        {
                            Logger.Log($"Update extracted to {outUpd}\n");
                        }
                    }
                }
                else
                {
                    if (singleModeCount)
                    {
                        Logger.LogErr($"[ERROR] --update requires a video or Redump ISO (got {iso})\n");
                        exit = 1;
                    }
                    else
                    {
                        Logger.Log($"[INFO] Skipping --update for {iso}\n");
                    }
                }
            }

            if (random)
            {
                // Extract filler
                string outFiller = (outputName != null && singleModeCount)
                    ? outputName
                    : Path.Combine(dir, baseName + ".filler");
                bool ok;
                if (isRedump)
                    ok = XisoOperations.ExtractFiller(iso, outFiller, isoOffset, xisoLen, Logger.Quiet);
                else
                    ok = XisoOperations.ExtractFiller(iso, outFiller, 0, null, Logger.Quiet);
                if (!ok)
                {
                    Logger.LogErr($"[ERROR] Failed extracting filler from {iso}\n");
                    exit = 1;
                }
                else
                {
                    Logger.Log($"Filler extracted to {outFiller} ({new FileInfo(outFiller).Length} bytes)\n");
                }
            }

            if (seed)
            {
                string outSeed = (outputName != null && singleModeCount)
                    ? outputName
                    : Path.Combine(dir, baseName + ".seed");
                bool ok = XisoOperations.TryExtractSeed(iso, outSeed, isRedump ? isoOffset : 0, Logger.Quiet);
                if (!ok)
                {
                    if (singleModeCount)
                    {
                        Logger.LogErr($"[ERROR] Failed extracting seed from {iso} (only XGD1)\n");
                        exit = 1;
                    }
                    else
                    {
                        Logger.Log($"[INFO] Skipping --seed for {iso} (only XGD1)\n");
                        try
                        {
                            if (File.Exists(outSeed)) File.Delete(outSeed);
                        }
                        catch
                        {
                            // ignored
                        }
                    }
                }
                else
                {
                    Logger.Log($"Seed extracted to {outSeed}\n");
                }
            }

            if (wipe)
            {
                string outWiped = (outputName != null && singleModeCount)
                    ? outputName
                    : Path.Combine(dir, baseName + ".wiped.xiso");
                bool ok;
                if (isRedump)
                {
                    // Produce wiped game partition from Redump
                    ok = XisoOperations.WipeFiller(iso, outWiped, isoOffset, Logger.Quiet);
                }
                else
                {
                    ok = XisoOperations.WipeFiller(iso, outWiped, 0, Logger.Quiet);
                }

                if (!ok)
                {
                    Logger.LogErr($"[ERROR] Failed wiping {iso}\n");
                    exit = 1;
                }
                else
                {
                    Logger.Log($"Wiped XISO written to {outWiped}\n");
                }

                // If trim also requested and wiping produced a file, trim that file instead of doing separate
                // To avoid double I/O when both --wipe and --trim are set, the batch will handle --best as wipe+trim
                // via WipeAndTrim below.
            }

            if (trim)
            {
                // If both wipe and trim are set (e.g. --best/--all), do combined operation to avoid double work
                bool combinedWipeTrim = wipe;
                if (combinedWipeTrim)
                {
                    string outTrimWiped = (outputName != null && singleModeCount)
                        ? outputName
                        : Path.Combine(dir, baseName + ".trim.wiped.xiso");
                    // The wiped file from previous step is at .wiped.xiso; we could do WipeAndTrim directly from original
                    string wipedPath = Path.Combine(dir, baseName + ".wiped.xiso");
                    // If we already produced wiped, trim it; else do combined
                    if (File.Exists(wipedPath) && !singleModeCount)
                    {
                        // Trim the already-wiped file
                        if (!XisoOperations.TrimXiso(wipedPath, outTrimWiped, 0, Logger.Quiet))
                        {
                            Logger.LogErr($"[ERROR] Failed trimming {wipedPath}\n");
                            exit = 1;
                        }
                        else
                        {
                            try { File.Delete(wipedPath); }
                            catch
                            {
                                // ignored
                            }

                            Logger.Log($"Wiped+trimmed XISO written to {outTrimWiped}\n");
                        }
                    }
                    else
                    {
                        string outPath2 = (outputName != null && singleModeCount)
                            ? outputName
                            : Path.Combine(dir, baseName + ".wiped.xiso");
                        // Do combined directly
                        bool ok = XisoOperations.WipeAndTrim(iso, outPath2, isRedump ? isoOffset : 0, Logger.Quiet);
                        if (!ok)
                        {
                            Logger.LogErr($"[ERROR] Failed wiping+trimming {iso}\n");
                            exit = 1;
                        }
                        else
                        {
                            Logger.Log($"Wiped+trimmed XISO written to {outPath2}\n");
                        }

                        // Skip separate trim below
                        continue;
                    }
                }
                else
                {
                    string outTrim = (outputName != null && singleModeCount)
                        ? outputName
                        : Path.Combine(dir, baseName + ".trim.xiso");
                    bool ok = XisoOperations.TrimXiso(iso, outTrim, isRedump ? isoOffset : 0, Logger.Quiet);
                    if (!ok)
                    {
                        Logger.LogErr($"[ERROR] Failed trimming {iso}\n");
                        exit = 1;
                    }
                    else
                    {
                        Logger.Log($"Trimmed XISO written to {outTrim}\n");
                    }
                }
            }

            if (petrify)
            {
                string outSkel = (outputName != null && singleModeCount)
                    ? outputName
                    : Path.Combine(dir, baseName + ".skeleton.xiso");
                string outHash = Path.Combine(dir, baseName + ".hash");
                // petrify already derives hash path internally if null, but we pass explicit
                bool ok = XisoSkeleton.Petrify(iso, outSkel, outHash, isRedump ? isoOffset : 0, Logger.Quiet);
                if (!ok)
                {
                    Logger.LogErr($"[ERROR] Failed petrifying {iso}\n");
                    exit = 1;
                }
                else
                {
                    Logger.Log($"Skeleton written to {outSkel}, hash to {outHash}\n");
                }
            }

            if (zar)
            {
                string outZar = (outputName != null && singleModeCount)
                    ? outputName
                    : Path.Combine(dir, baseName + ".zar");
                bool ok = XisoZarchive.CreateZar(iso, outZar, isRedump ? isoOffset : 0, Logger.Quiet);
                if (!ok)
                {
                    Logger.LogErr($"[ERROR] Failed creating ZAR for {iso}\n");
                    exit = 1;
                }
                else
                {
                    Logger.Log($"ZAR written to {outZar}\n");
                }
            }
        }

        return exit;
    }

    /// <summary>
    /// Translates a <c>--pack</c> input: a directory becomes a create-mode entry, an
    /// existing ISO file becomes an in-place rewrite. Returns 0 on success, 1 on error
    /// (the message is logged). Invalid combinations are rejected.
    /// </summary>
    private static int TranslatePackInput(
        string? packInput,
        string? packName,
        string? batchDir,
        bool rewrite,
        bool info,
        bool lsMode,
        bool xexInfoMode,
        bool unpackMode,
        bool hashMode,
        bool copyOut,
        bool auditMode,
        bool validateMode,
        bool tree,
        bool extract,
        int optind,
        int argsLength,
        List<(string Dir, string? Name)> createList,
        ref bool rewriteFlag,
        ref string? packIsoFile,
        ref string? outputPath)
    {
        if (packInput == null)
        {
            return 0;
        }

        if (rewrite || info || lsMode || xexInfoMode || unpackMode || hashMode || copyOut || auditMode ||
            validateMode || tree || !extract)
        {
            Logger.LogErr("Error: --pack cannot be combined with other modes\n");
            return 1;
        }

        if (batchDir != null)
        {
            Logger.LogErr("Error: --pack cannot be combined with --batch\n");
            return 1;
        }

        if (optind < argsLength)
        {
            Logger.LogErr("Error: --pack takes the input as an argument; extra filenames are not allowed\n");
            return 1;
        }

        if (Directory.Exists(packInput))
        {
            if (outputPath != null)
            {
                Logger.LogErr("Error: --pack <dir> does not use -d; put the destination path in the output name\n");
                return 1;
            }

            createList.Add((packInput, packName));
        }
        else if (File.Exists(packInput))
        {
            if (packName != null)
            {
                Logger.LogErr("Error: --pack <iso> rewrites the image in place and does not take an output name\n");
                return 1;
            }

            rewriteFlag = true;
            packIsoFile = packInput;

            // Repack in place: default the rewrite output to the source's directory
            // (an explicit -d still wins).
            outputPath ??= Path.GetDirectoryName(Path.GetFullPath(packInput));
        }
        else
        {
            Logger.LogErr($"Error: {packInput} is not a directory or an ISO file\n");
            return 1;
        }

        return 0;
    }

    /// <summary>
    /// Resolves the list of ISO files to process: explicit filenames, a <c>--batch</c>
    /// directory scan, or a <c>--pack</c> ISO input. Returns <c>null</c> (after logging
    /// the error) when the inputs are invalid.
    /// </summary>
    private static List<string>? ExpandIsoFiles(
        string? batchDir,
        bool batchRecursive,
        string[] args,
        int optind,
        string? packIsoFile)
    {
        if (batchDir != null)
        {
            if (optind < args.Length)
            {
                Logger.LogErr("Error: --batch cannot be combined with explicit ISO filenames\n");
                return null;
            }

            try
            {
                // Case-insensitive *.iso matching on every platform (the SearchOption
                // overload would be case-sensitive on Linux/macOS).
                var options = new EnumerationOptions
                {
                    MatchCasing = MatchCasing.CaseInsensitive,
                    RecurseSubdirectories = batchRecursive,
                    AttributesToSkip = FileAttributes.None // include hidden files, like the SearchOption overload
                };
                var isoFiles = Directory.EnumerateFiles(batchDir, "*.iso", options)
                    .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                if (isoFiles.Count == 0)
                {
                    Logger.LogErr($"Error: no .iso files found in {batchDir}\n");
                    return null;
                }

                Logger.Log(
                    $"batch: processing {isoFiles.Count} ISO file(s) from {batchDir}{(batchRecursive ? " (recursive)" : "")}\n");
                return isoFiles;
            }
            catch (Exception ex) when (ex is DirectoryNotFoundException or UnauthorizedAccessException or IOException)
            {
                Logger.LogErr($"Error: cannot read batch directory {batchDir}: {ex.Message}\n");
                return null;
            }
        }

        var files = args.Skip(optind).ToList();
        if (packIsoFile != null)
        {
            files.Insert(0, packIsoFile);
        }

        return files;
    }

    /// <summary>
    /// Formats the XEX module flags as a comma-separated list of names.
    /// </summary>
    private static string FormatXexModuleFlags(uint flags)
    {
        var parts = new List<string>();
        if ((flags & 0x01) != 0) parts.Add("Title");
        if ((flags & 0x02) != 0) parts.Add("ExportsToTitle");
        if ((flags & 0x04) != 0) parts.Add("SystemDebugger");
        if ((flags & 0x08) != 0) parts.Add("DllModule");
        if ((flags & 0x10) != 0) parts.Add("ModulePatch");
        if ((flags & 0x20) != 0) parts.Add("PatchFull");
        if ((flags & 0x40) != 0) parts.Add("PatchDelta");
        if ((flags & 0x80) != 0) parts.Add("UserMode");
        return parts.Count > 0 ? $" ({string.Join(", ", parts)})" : "";
    }

    /// <summary>
    /// Formats the XEX region flags as the best-matching region name.
    /// </summary>
    private static string FormatXexRegion(uint region)
    {
        if (region == 0xFFFFFFFF) return " (All)";
        if ((region & 0x000000FF) != 0) return " (NTSC-U)";
        if ((region & 0x0000FF00) != 0) return " (NTSC-J)";
        if ((region & 0x00FF0000) != 0) return " (PAL)";
        if ((region & 0xFF000000) != 0) return " (Other)";

        return "";
    }

    /// <summary>
    /// Formats the XEX allowed-media-types bitmask as a comma-separated list of names.
    /// </summary>
    private static string FormatXexMediaTypes(uint media)
    {
        var parts = new List<string>();
        if ((media & 0x00000001) != 0) parts.Add("HardDisk");
        if ((media & 0x00000002) != 0) parts.Add("DvdX2");
        if ((media & 0x00000004) != 0) parts.Add("DvdCd");
        if ((media & 0x00000008) != 0) parts.Add("Dvd5");
        if ((media & 0x00000010) != 0) parts.Add("Dvd9");
        if ((media & 0x00000020) != 0) parts.Add("SystemFlash");
        if ((media & 0x00000080) != 0) parts.Add("MemoryUnit");
        if ((media & 0x00000100) != 0) parts.Add("UsbMassStorage");
        if ((media & 0x00000200) != 0) parts.Add("Network");
        if ((media & 0x00000400) != 0) parts.Add("DirectFromMemory");
        if ((media & 0x00000800) != 0) parts.Add("RamDrive");
        if ((media & 0x00001000) != 0) parts.Add("Svod");
        if ((media & 0x01000000) != 0) parts.Add("InsecurePackage");
        if ((media & 0x02000000) != 0) parts.Add("SavegamePackage");
        if ((media & 0x04000000) != 0) parts.Add("LocallySignedPackage");
        if ((media & 0x08000000) != 0) parts.Add("LiveSignedPackage");
        if ((media & 0x10000000) != 0) parts.Add("XboxPackage");
        return parts.Count > 0 ? $" ({string.Join(", ", parts)})" : "";
    }

    /// <summary>Formats the XEX encryption type as a name.</summary>
    private static string FormatXexEncryption(ushort encryption) => encryption switch
    {
        0 => "None",
        1 => "Normal",
        _ => "Unknown"
    };

    /// <summary>Formats the XEX compression type as a name.</summary>
    private static string FormatXexCompression(ushort compression) => compression switch
    {
        0 => "None",
        1 => "Basic",
        2 => "Normal",
        3 => "Delta",
        _ => "Unknown"
    };

    /// <summary>
    /// Formats a directory entry attribute byte as a human-readable string.
    /// </summary>
    private static string FormatAttributes(byte attrs)
    {
        var parts = new List<string>();
        if ((attrs & Constants.AttributeDir) != 0) parts.Add("Directory");
        if ((attrs & Constants.AttributeRo) != 0) parts.Add("ReadOnly");
        if ((attrs & Constants.AttributeHid) != 0) parts.Add("Hidden");
        if ((attrs & Constants.AttributeSys) != 0) parts.Add("System");
        if ((attrs & Constants.AttributeArc) != 0) parts.Add("Archive");
        if ((attrs & Constants.AttributeNor) != 0) parts.Add("Normal");
        return parts.Count > 0 ? $" ({string.Join(", ", parts)})" : "";
    }

    /// <summary>
    /// Prints the usage/help text to standard error.
    /// </summary>
    private static void PrintUsage()
    {
        Console.Error.Write(Constants.Banner + """
                                                 Usage:
                                                 Usage:

                                                   extract-xiso [options] [-[lrx]] <file1.xiso> [file2.xiso] ...
                                                   extract-xiso [options] -c <dir> [name] [-c <dir> [name]] ...

                                                   Mutually exclusive modes:

                                                     -c <dir> [name]     Create xiso from file(s) starting in <dir>.
                                                     --copy-out <iso> <path> <dest>  Copy a file or directory out of an xiso.
                                                     -i <file> [path]    Show volume info and directory entry metadata.
                                                     --ls <file> [path]   List the entries of a directory (default root)
                                                                           without recursion. Mirrors 'ls' on the image.
                                                     --xex-info <file> <path>  Show the Xbox 360 XEX2 executable
                                                                           header of a .xex file inside the image
                                                                           (module flags, entry point, title ID, ...).
                                                     -l                  List files in xiso(s).
                                                     --md5 <file> [path] Compute MD5 hash of file(s) in xiso.
                                                     -r                  Rewrite xiso(s) as optimized xiso(s).
                                                     --sha256 <file> [path] Compute SHA-256 hash of file(s) in xiso.
                                                     -t                  List all files recursively with sizes (tree).
                                                     -V <file1.xiso> ...  Deep-audit xiso(s): validate header, tree, sectors.
                                                     --batch <dir>        Process all .iso files in <dir> instead of
                                                                           explicit filenames. Works with extract,
                                                                           list, tree, rewrite (-r), and audit (-V).
                                                     --batch-recursive    With --batch, search subdirectories recursively.
                                                     --pack <input> [name]  Pack a directory into an ISO (name defaults
                                                                           to the directory name; may include a path),
                                                                           or repack an existing ISO in place (rewrite).
                                                     validate <src> <out> Validate conversion by comparing source and output ISOs.
                                                     -x                  Extract xiso(s) (the default mode if none is given).
                                                     --unpack <file> [dest]  Unpack the whole image to <dest>, or to a
                                                                           directory named after the ISO when omitted.
                                                     -X <glob_pattern>   In create mode (-c), exclude files/directories
                                                                           matching the glob pattern from the image.
                                                                           Repeatable. Examples: "*.tmp", "**/node_modules/**",
                                                                           "screenshots/**". Use "/" as the path separator.
                                                                           With -s, $SystemUpdate is excluded automatically.

                                                     Redump / XboxKit modes (lossless archival):

                                                     rebuild <xiso> [video.iso] [filler|seed] [su...] -o <redump.iso>
                                                                           Rebuild a Redump ISO from its components.
                                                                           Auto-detects XGD type by size; video type via
                                                                           PVD at 0x832D. Supports filler file or 4-byte
                                                                           seed (XGD1 PRNG) plus optional sectors.txt for
                                                                           security sectors (--security-sectors).
                                                     --video <redump.iso> [video.iso]  Extract video partition (L0+L1).
                                                     --random <iso> [filler.bin]  Extract random filler gaps.
                                                     --seed <iso> [seed.bin]    Extract XGD1 PRNG seed (brute-force, 4 bytes).
                                                     --wipe <iso> [wiped.xiso]  Write XISO with filler zeroed.
                                                     --trim <iso> [trimmed.xiso]  Trim XISO after last file extent.
                                                     --petrify <iso> [skeleton.xiso] [hash]  Zero file data, emit SHA1 hashes.
                                                     --update <video.iso|redump.iso> [update]  Extract su20076000_00000000 (XGD3) and zero it in video.
                                                     --zar <iso> [out.zar]      Create ZArchive (zstd blocks, raw fallback; trimmable).
                                                     --all <redump.iso>         Alias: --random --seed --trim --update --video --wipe
                                                     --best <iso>               Alias: --trim --wipe
                                                     --compress <iso>           Alias: --petrify --update --video --zar
                                                      --security-sectors <sectors.txt>  Override security sector ranges (start-end, 4096 sectors).

                                                      XDVDFS / Packing modes (ordered remapping):

                                                      build-image [sourceDir] [output.iso] -f <xdvdfs.toml> -m "hostGlob:imagePath" [-O output] [-D|--dry-run]
                                                                            Pack an image with ordered wax-glob remapping ({0} whole match, {1..n} per '*'/'**' capture, '!' exclusion). Examples:
                                                                              build-image -m "bin:/" -m "assets/**:/assets/{1}" ./src dist/final.xiso.iso
                                                                              build-image --dry-run -f xdvdfs.toml ./src
                                                                            Globs support '*', '?', '**', '[]', '{a,b}'. Order matters; first match wins, negation clears and allows re-inclusion.
                                                                            When -m is used, -f is ignored; -O sets output when no positional is given. Dry-run prints "host -> image".
                                                      image-spec from -O <out> -m <host:image> ... [specPath]
                                                                            Generate an xdvdfs.toml from map rules (stdout when omitted, file when given). Example:
                                                                              image-spec from -O dist/image.xiso.iso -m "bin:/" -m "assets/**:/{0}" xdvdfs.toml

                                                    Options:

                                                    -d <directory>      In extract mode, expand xiso in <directory>.
                                                                        In rewrite mode, rewrite xiso in <directory>.
                                                    -D                  In rewrite mode, delete old xiso after processing.
                                                    -h                  Print this help text and exit.
                                                    -m                  In create or rewrite mode, disable automatic .xbe
                                                                          media enable patching (not recommended).
                                                    -o <filename>       In rewrite mode, set custom output filename
                                                                          (default: original name with .iso extension).
                                                    --skip-sectors N     Treat the image as if the XISO filesystem
                                                                          begins N sectors (2048 bytes each) into the
                                                                          file. Use for Redump images where a video
                                                                          partition precedes the game partition.
                                                                          Valid in extract, list, tree, and rewrite mode.
                                                    --prepend-sectors N  Write the output image with N empty sectors
                                                                          before the XISO filesystem, leaving room for
                                                                          a video partition. Valid in create (-c) and
                                                                          rewrite (-r) mode. Combine with --skip-sectors
                                                                          for round-trip Redump-style reconstruction.
                                                    -q                  Run quiet (suppress all non-error output).
                                                    -Q                  Run silent (suppress all output).
                                                    -s                  Skip $SystemUpdate folder.
                                                    -v                  Print version information and exit.

                                                    Validation options (with -r or validate command):

                                                    --validate          Enable post-conversion validation after rewrite.
                                                    --validate-checksums  Also verify SHA-256 checksums (slower).
                                                    --validate-strict   Fail with exit code 2 on any mismatch.
                                                    --validate-report <file>  Write JSON validation report to file.

                                               """);
    }
}