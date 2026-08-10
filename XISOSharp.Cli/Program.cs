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

        var optind = 0;

        // Handle standalone 'validate' command early (doesn't start with '-')
        if (args.Length > 0 && string.Equals(args[0], "validate", StringComparison.OrdinalIgnoreCase))
        {
            validateMode = true;
            extract = false;
            optind = 1;
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
                        if (i + 1 < args.Length && int.TryParse(args[i + 1], CultureInfo.InvariantCulture, out var skipVal) && skipVal >= 0)
                        {
                            skipSectors = skipVal;
                            i++;
                        }
                        else
                        {
                            Logger.LogErr("Error: --skip-sectors requires a non-negative integer (number of 2048-byte sectors)\n");
                            return 1;
                        }

                        break;
                    case "--prepend-sectors":
                        if (i + 1 < args.Length && int.TryParse(args[i + 1], CultureInfo.InvariantCulture, out var prependVal) && prependVal >= 0)
                        {
                            prependSectors = prependVal;
                            i++;
                        }
                        else
                        {
                            Logger.LogErr("Error: --prepend-sectors requires a non-negative integer (number of 2048-byte sectors)\n");
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
            Logger.LogErr("Error: --skip-sectors/--prepend-sectors are only supported in extract, list, tree, rewrite (-r), unpack, and create (-c) modes\n");
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

        if (batchDir != null && (createList.Count > 0 || info || lsMode || xexInfoMode || unpackMode || hashMode || copyOut || validateMode))
        {
            Logger.LogErr("Error: --batch is only supported in extract, list, tree, rewrite (-r), and audit (-V) modes\n");
            return 1;
        }

        if (unpackMode && (info || lsMode || xexInfoMode || tree || hashMode || copyOut || auditMode || validateMode))
        {
            Logger.LogErr("Error: --unpack cannot be combined with other modes\n");
            return 1;
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
                        Logger.Log($"    L-Offset:  {(entry.LeftChildOffset == 0 ? "none" : entry.LeftChildOffset.ToString())}\n");
                        Logger.Log($"    R-Offset:  {(entry.RightChildOffset == 0 ? "none" : entry.RightChildOffset.ToString())}\n");
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
                Logger.Log($"  Media types:       0x{xex.AllowedMediaTypes:X8}{FormatXexMediaTypes(xex.AllowedMediaTypes)}\n");
                Logger.Log($"  Media ID:          0x{xex.MediaId:X8}\n");
                Logger.Log($"  Title ID:          0x{xex.TitleId:X8}\n");
                Logger.Log($"  Version:           0x{xex.Version:X8}\n");
                Logger.Log($"  Platform:          0x{xex.Platform:X2}\n");
                Logger.Log($"  Disc:              {xex.DiscNumber}/{xex.DiscCount}\n");
                Logger.Log($"  Encryption:        {xex.EncryptionType} ({FormatXexEncryption(xex.EncryptionType)})\n");
                Logger.Log($"  Compression:       {xex.CompressionType} ({FormatXexCompression(xex.CompressionType)})\n");
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
                    XisoReader.DecodeXiso(oldPath, path, ExtractMode.Rewrite, out var newIsoPath, true, outputName: outputName, skipSectors: skipSectors, prependSectors: prependSectors);

                    if (err == 0)
                    {
                        Logger.Log($"\n{Logger.TotalFiles} files in {newIsoPath} total {Logger.TotalBytes} bytes\n");
                        Logger.Log($"\n{xisoPath} successfully rewritten{(path != null ? " as " : ".")}{(path != null ? newIsoPath : "")}\n");
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
                    Logger.LogErr($"failed to {(extract ? "extract" : tree ? "tree" : "list")} xbox iso image {xisoPath}: {ex.Message}\n");
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

        if (rewrite || info || lsMode || xexInfoMode || unpackMode || hashMode || copyOut || auditMode || validateMode || tree || !extract)
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

                Logger.Log($"batch: processing {isoFiles.Count} ISO file(s) from {batchDir}{(batchRecursive ? " (recursive)" : "")}\n");
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
        Console.Error.Write($"""
                             {Constants.Banner}
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
