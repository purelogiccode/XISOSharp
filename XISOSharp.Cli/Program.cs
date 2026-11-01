using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Serilog;
using XISOSharp.Cli.Logging;
using XISOSharp.Models;
using ZArchiveSharp.Pipeline;

namespace XISOSharp.Cli;

/// <summary>
/// Command-line entry point for the XISOSharp CLI (extract-xiso-compatible).
/// Parses arguments and dispatches to <see cref="XisoReader"/> for extraction/listing/rewriting
/// or <see cref="XisoWriter"/> for image creation.
/// </summary>
internal static class Program
{
    /// <summary>Serializes parallel <c>--zar</c> batch summaries.</summary>
#if NET9_0_OR_GREATER
    private static readonly Lock LogLock = new();
#else
    private static readonly object LogLock = new();
#endif

    /// <summary>
    /// Parses a <c>--policy</c> value (<c>skip</c> / <c>overwrite</c> /
    /// <c>auto-rename</c>) into a <see cref="ZarCollisionPolicy"/>.
    /// </summary>
    private static bool TryParseCollisionPolicy(string text, out ZarCollisionPolicy? policy)
    {
        if (string.Equals(text, "skip", StringComparison.OrdinalIgnoreCase))
        {
            policy = ZarCollisionPolicy.Skip;
            return true;
        }

        if (string.Equals(text, "overwrite", StringComparison.OrdinalIgnoreCase))
        {
            policy = ZarCollisionPolicy.Overwrite;
            return true;
        }

        if (string.Equals(text, "auto-rename", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(text, "autorename", StringComparison.OrdinalIgnoreCase))
        {
            policy = ZarCollisionPolicy.AutoRename;
            return true;
        }

        policy = null;
        return false;
    }

    /// <summary>
    /// Emits UTF-8 on stdout/stderr regardless of the host console code page so
    /// Latin-1 filenames render and pipe identically on every platform (and match
    /// xdvdfs's UTF-8 output). Best-effort: hosts that refuse the change keep the
    /// default encoding.
    /// </summary>
    private static void ConfigureConsoleEncoding()
    {
        try
        {
            Console.OutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        }
        catch (Exception ex) when (ex is IOException or PlatformNotSupportedException)
        {
            // Redirected/no-console hosts that reject the code-page change.
        }
    }

    /// <summary>
    /// Entry point. Parses command-line flags and positional arguments,
    /// then invokes the appropriate XISO operation.
    /// Internal (not private) so the test suite can drive end-to-end CLI runs.
    /// </summary>
    /// <param name="args">Command-line arguments.</param>
    /// <returns>0 on success, 1 on error.</returns>
    internal static int Main(string[] args)
    {
        ConfigureConsoleEncoding();
        AppLogging.Configure("XISOSharp");
        // Machine-readable output (--json) owns stdout: route every human log to
        // stderr for the duration of the run (PlanToEnhance Phase 6).
        bool jsonRequested = Array.Exists(args, static a => string.Equals(a, "--json", StringComparison.Ordinal));
        bool priorRouteInfoToError = Logger.RouteInfoToError;
        if (jsonRequested) Logger.RouteInfoToError = true;
        // Launch ping for usage stats (fire-and-forget, opt out with
        // XISO_DISABLE_STATS=1; test hosts are skipped).
        ApplicationStats.RecordLaunch("xisosharp");
        // Daily GitHub update check (stderr notice + optional browser
        // redirect). Skipped for quiet/version runs, test hosts, offline
        // machines pay at most one short timeout per day — never fails the run.
        UpdateChecker.CheckForUpdates(args);
        // Launched by double-click (Explorer: no arguments on an interactive
        // console): keep the window open at the end so the usage text stays
        // readable instead of flashing away.
        bool pauseBeforeExit = args.Length == 0 && IsInteractiveConsoleLaunch();
        try
        {
            return MainInner(args);
        }
        catch (NotSupportedException ex)
        {
            // Expected user error (for example a .zar archive passed to a
            // sector-only verb): report it without a stack trace.
            Logger.LogErr($"Error: {ex.Message}\n");
            return 1;
        }
        catch (Exception ex)
        {
            try
            {
                Log.Fatal(ex, "Unhandled CLI exception");
            }
            catch
            {
                // Logging must never throw from the top-level handler.
            }

            Logger.LogErr($"Fatal error: {ex.Message}\n");
            return 1;
        }
        finally
        {
            Logger.RouteInfoToError = priorRouteInfoToError;
            // X-003: deliver pending bug-report sends before the process exits;
            // a short-lived CLI would otherwise drop crash reports every time.
            try
            {
                BugReporter.Flush(TimeSpan.FromSeconds(2));
                ApplicationStats.Flush(TimeSpan.FromSeconds(2));
            }
            catch
            {
                // Reporting must never fail the CLI.
            }

            AppLogging.CloseAndFlush();

            if (pauseBeforeExit)
                PauseBeforeExit();
        }
    }

    /// <summary>
    /// Reports whether the process was launched interactively with no console
    /// redirection — the double-click-from-Explorer case. Never true under test
    /// hosts (which drive <see cref="Main"/> directly) or when I/O is piped.
    /// </summary>
    private static bool IsInteractiveConsoleLaunch()
    {
        try
        {
            if (!Environment.UserInteractive)
                return false;
            if (Console.IsInputRedirected || Console.IsOutputRedirected)
                return false;
            if (string.Equals(Environment.GetEnvironmentVariable("XISO_NO_PAUSE"), "1",
                    StringComparison.Ordinal))
            {
                return false;
            }

            string? entry = Assembly.GetEntryAssembly()?.GetName().Name;
            if (entry?.Contains("test", StringComparison.OrdinalIgnoreCase) == true)
                return false;
            if (AppDomain.CurrentDomain.FriendlyName.Contains("test", StringComparison.OrdinalIgnoreCase))
                return false;
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Waits for a key press before exiting so a double-clicked console window
    /// stays readable. Never throws.
    /// </summary>
    private static void PauseBeforeExit()
    {
        try
        {
            Console.WriteLine();
            Console.WriteLine("Press any key to exit...");
            _ = Console.ReadKey(intercept: true);
        }
        catch
        {
            // No console to pause on — exit normally.
        }
    }

    private static int MainInner(string[] args)
    {
        if (args.Length < 1)
        {
            PrintUsage();
            return 1;
        }

        bool extract = true;
        bool rewrite = false;
        bool listMode = false;
        bool tree = false;
        bool info = false;
        bool lsMode = false;
        bool xexInfoMode = false;
        bool xbeInfoMode = false;
        bool repairMode = false;
        bool repairDryRun = false;
        bool salvageMode = false;
        string? repairOut = null;
        bool unpackMode = false;
        bool hashMode = false;
        bool copyOut = false;
        bool copyIn = false;
        bool copyInNoBackup = false;
        bool auditMode = false;
        bool validateMode = false;
        bool checksumFlagMode = false;
        bool checksumSilent = false;
        bool silentFlag = false;
        string? hashAlgo = null;
        bool xSeen = false;
        bool deleteOld = false;
        bool preserveAttrs = false;
        bool assumeYes = false;
        bool assumeNo = false;
        string? path = null;
        string? outputName = null;
        List<(string Dir, string? Name)> createList = new();
        int isos = 0;
        int err = 0;

        bool validateFlag = false;
        bool validateChecksums = false;
        bool validateStrict = false;
        string? validateReport = null;

        int? skipSectors = null;
        int? prependSectors = null;
        ulong? createFileTime = null;
        List<string> excludePatterns = new();
        string? batchDir = null;
        bool batchRecursive = false;
        bool skipExisting = false;
        bool continueOnError = false;
        string? packInput = null;
        string? packName = null;
        string? packIsoFile = null;
        string? layoutFile = null;

        bool filetimeMode = false;
        bool setFiletimeMode = false;
        bool sectorLayoutMode = false;
        bool rangesMode = false;
        bool isOptimizedMode = false;
        bool jsonMode = false;

        // XboxKit redump / archival modes
        bool videoMode = false;
        bool randomMode = false;
        bool seedMode = false;
        bool wipeMode = false;
        bool trimMode = false;
        bool petrifyMode = false;
        bool updateMode = false;
        bool zarMode = false;
        bool allMode = false;
        bool bestMode = false;
        bool compressAlias = false;
        int jobs = 1;
        ZarCollisionPolicy? zarPolicy = null;
        string? securitySectorsPath = null;

        int optind = 0;

        // CLI-002 order-independence for -v/-h: those return mid-parse and
        // cannot consult the parsed silentFlag/checksumFlagMode (Todo #32).
        // Scan the raw arguments so `--silent -v --checksum` is accepted in
        // any order instead of failing on the flags seen so far.
        bool silentRequestedAnywhere =
            Array.Exists(args, static a => string.Equals(a, "--silent", StringComparison.Ordinal));
        bool checksumRequestedAnywhere =
            Array.Exists(args, static a => string.Equals(a, "--checksum", StringComparison.Ordinal));
        bool jsonRequestedAnywhere =
            Array.Exists(args, static a => string.Equals(a, "--json", StringComparison.Ordinal));

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
        else if (args.Length > 0 && (string.Equals(args[0], "compress", StringComparison.OrdinalIgnoreCase) ||
                                     string.Equals(args[0], "cso", StringComparison.OrdinalIgnoreCase) ||
                                     string.Equals(args[0], "cci", StringComparison.OrdinalIgnoreCase)))
        {
            string defaultFormat = string.Equals(args[0], "cci", StringComparison.OrdinalIgnoreCase)
                ? "cci"
                : "cso";
            return RunCompressMode(args, 1, defaultFormat);
        }
        else if (args.Length > 0 && (string.Equals(args[0], "decompress", StringComparison.OrdinalIgnoreCase) ||
                                     string.Equals(args[0], "uncso", StringComparison.OrdinalIgnoreCase) ||
                                     string.Equals(args[0], "decso", StringComparison.OrdinalIgnoreCase) ||
                                     string.Equals(args[0], "uncci", StringComparison.OrdinalIgnoreCase)))
        {
            return RunDecompressMode(args, 1);
        }
        else if (args.Length > 0 && (string.Equals(args[0], "checksum", StringComparison.OrdinalIgnoreCase) ||
                                     string.Equals(args[0], "--checksum", StringComparison.OrdinalIgnoreCase)))
        {
            return RunChecksumMode(args, 1);
        }
        else if (args.Length > 0 && string.Equals(args[0], "split", StringComparison.OrdinalIgnoreCase))
        {
            return RunSplitMode(args, 1);
        }
        else if (args.Length > 0 && (string.Equals(args[0], "join", StringComparison.OrdinalIgnoreCase) ||
                                     string.Equals(args[0], "joinsplit", StringComparison.OrdinalIgnoreCase)))
        {
            return RunJoinMode(args, 1);
        }
        else if (args.Length > 0 && string.Equals(args[0], "convert", StringComparison.OrdinalIgnoreCase))
        {
            return RunConvertMode(args, 1);
        }

        for (int i = optind; i < args.Length; i++)
        {
            string arg = args[i];
            if (arg.StartsWith('-') && arg.Length > 1)
            {
                switch (arg)
                {
                    case "-v":
                    case "--version":
                        if (silentRequestedAnywhere && !checksumRequestedAnywhere)
                        {
                            Logger.LogErr("Error: --silent requires --checksum\n");
                            return 1;
                        }

                        if (jsonRequestedAnywhere)
                        {
                            WriteJsonVersion();
                            return 0;
                        }

                        WriteVersionInfo();
                        return 0;
                    case "-h":
                    case "--help":
                        if (silentRequestedAnywhere && !checksumRequestedAnywhere)
                        {
                            Logger.LogErr("Error: --silent requires --checksum\n");
                            return 1;
                        }

                        PrintUsage();
                        return 0;
                    case "-c":
                    {
                        if (xSeen || rewrite || !extract || i + 1 >= args.Length)
                        {
                            PrintUsage();
                            return 1;
                        }

                        string dir = args[++i];
                        string? name = null;
                        if (i + 1 < args.Length && !args[i + 1].StartsWith('-'))
                        {
                            name = args[++i];
                        }

                        createList.Add((dir, name));
                        break;
                    }
                    case "-x":
                    {
                        // CLI-002: -x is order-independent. It is the default, so a
                        // repeat is harmless, but after any operational mode (or
                        // -r/-c) it is a conflict, not a silent no-op.
                        if (xSeen)
                            break;
                        if (rewrite || !extract || createList.Count > 0 || ClassicModeSelected())
                        {
                            PrintUsage();
                            return 1;
                        }

                        xSeen = true;
                        break;
                    }
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
                        listMode = true;
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
                    case "--xbe-info":
                        if (xSeen || rewrite || createList.Count > 0)
                        {
                            PrintUsage();
                            return 1;
                        }

                        extract = false;
                        xbeInfoMode = true;
                        break;
                    case "--repair":
                        if (xSeen || rewrite || createList.Count > 0)
                        {
                            PrintUsage();
                            return 1;
                        }

                        extract = false;
                        repairMode = true;
                        break;
                    case "--salvage":
                        if (xSeen || rewrite || createList.Count > 0)
                        {
                            PrintUsage();
                            return 1;
                        }

                        extract = false;
                        salvageMode = true;
                        break;
                    case "--md5":
                        // CLI-001: --md5/--sha256 share hashMode; a second hash
                        // selection would silently overwrite the first.
                        if (hashMode || xSeen || rewrite || createList.Count > 0)
                        {
                            PrintUsage();
                            return 1;
                        }

                        extract = false;
                        hashMode = true;
                        hashAlgo = "MD5";
                        break;
                    case "--sha256":
                        if (hashMode || xSeen || rewrite || createList.Count > 0)
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
                        // CLI-003: the flavor flags imply --validate so they
                        // can never be set without effect (GUI-002 parity).
                        validateFlag = true;
                        validateChecksums = true;
                        break;
                    case "--validate-strict":
                        validateFlag = true;
                        validateStrict = true;
                        break;
                    case "--validate-report":
                        validateFlag = true;
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
                    case "--copy-in":
                        if (xSeen || rewrite || createList.Count > 0)
                        {
                            PrintUsage();
                            return 1;
                        }

                        extract = false;
                        copyIn = true;
                        break;
                    case "--no-backup":
                        copyInNoBackup = true;
                        break;
                    case "--dry-run":
                        repairDryRun = true;
                        break;
                    case "--repair-out":
                        if (i + 1 < args.Length)
                        {
                            repairOut = args[++i];
                        }
                        else
                        {
                            PrintUsage();
                            return 1;
                        }

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
                    case "-y":
                    case "--yes":
                        assumeYes = true;
                        break;
                    case "-n":
                    case "--no":
                        assumeNo = true;
                        break;
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
                    case "--skip-sectors":
                        if (i + 1 < args.Length &&
                            int.TryParse(args[i + 1], CultureInfo.InvariantCulture, out int skipVal) && skipVal >= 0)
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
                            int.TryParse(args[i + 1], CultureInfo.InvariantCulture, out int prependVal) &&
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
                    case "--preserve-attrs":
                        preserveAttrs = true;
                        break;
                    case "--file-time":
                        if (i + 1 < args.Length &&
                            FileTimeHelper.TryParseFileTime(args[i + 1], out ulong createRaw, out _))
                        {
                            createFileTime = createRaw;
                            i++;
                        }
                        else
                        {
                            Logger.LogErr(
                                "Error: --file-time requires a value (ISO-8601, decimal raw, 0x hex, 'now', or '0' for deterministic images)\n");
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
                    case "--skip-existing":
                        skipExisting = true;
                        break;
                    case "--continue-on-error":
                        continueOnError = true;
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
                    case "--layout":
                        if (i + 1 >= args.Length || args[i + 1].StartsWith('-'))
                        {
                            Logger.LogErr("Error: --layout requires a layout file\n");
                            return 1;
                        }

                        layoutFile = args[++i];
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
                    case "--jobs":
                        if (i + 1 >= args.Length || !int.TryParse(args[i + 1],
                                NumberStyles.Integer, CultureInfo.InvariantCulture, out jobs) || jobs < 1)
                        {
                            PrintUsage();
                            return 1;
                        }

                        i++;
                        break;
                    case "--policy":
                        if (i + 1 >= args.Length || !TryParseCollisionPolicy(args[i + 1], out zarPolicy))
                        {
                            PrintUsage();
                            return 1;
                        }

                        i++;
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
                    case "--checksum":
                        if (xSeen || rewrite || createList.Count > 0)
                        {
                            PrintUsage();
                            return 1;
                        }

                        extract = false;
                        checksumFlagMode = true;
                        break;
                    case "--filetime":
                    case "--get-filetime":
                        if (xSeen || rewrite || createList.Count > 0)
                        {
                            PrintUsage();
                            return 1;
                        }

                        extract = false;
                        filetimeMode = true;
                        break;
                    case "--set-filetime":
                        if (xSeen || rewrite || createList.Count > 0)
                        {
                            PrintUsage();
                            return 1;
                        }

                        extract = false;
                        setFiletimeMode = true;
                        break;
                    case "--sector-layout":
                        if (xSeen || rewrite || createList.Count > 0)
                        {
                            PrintUsage();
                            return 1;
                        }

                        extract = false;
                        sectorLayoutMode = true;
                        break;
                    case "--ranges":
                        if (xSeen || rewrite || createList.Count > 0)
                        {
                            PrintUsage();
                            return 1;
                        }

                        extract = false;
                        rangesMode = true;
                        break;
                    case "--is-optimized":
                        if (xSeen || rewrite || createList.Count > 0)
                        {
                            PrintUsage();
                            return 1;
                        }

                        extract = false;
                        isOptimizedMode = true;
                        break;
                    case "--silent":
                        // Recorded here and validated after the whole command line
                        // is parsed: consulting checksumFlagMode while parsing made
                        // `--silent --checksum` fail while `--checksum --silent`
                        // worked (Todo #32).
                        silentFlag = true;
                        break;
                    case "--json":
                        jsonMode = true;
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

        if (jsonMode)
        {
            bool readOnlyJsonMode = info || listMode || tree || checksumFlagMode || sectorLayoutMode ||
                                    rangesMode || filetimeMode || xexInfoMode || xbeInfoMode || validateMode;
            if (!readOnlyJsonMode)
            {
                Logger.LogErr(
                    "Error: --json requires a read-only mode (-i, -l, -t, --checksum, --sector-layout, --ranges, --filetime, --xex-info, --xbe-info, or validate)\n");
                return 1;
            }
        }

        // Upstream #61: `game.iso -d ./new/` silently probed `-d` as an image
        // ("open error: -d"). Flags must precede filenames, so name the mistake
        // instead. CLI-025: no existence bypass — a file literally named like a
        // flag is still reachable as ./-y or by absolute path.
        for (int i = optind; i < args.Length; i++)
        {
            string? misplaced = CliOutputGuard.CheckMisplacedFlag(args[i]);
            if (misplaced != null)
            {
                Logger.LogErr(misplaced);
                return 1;
            }
        }

        // --pack translates to create mode (directory input) or rewrite mode (ISO input),
        // reusing the existing create/rewrite machinery.
        if (TranslatePackInput(packInput, packName, batchDir, rewrite, info, lsMode, xexInfoMode, xbeInfoMode,
                repairMode, salvageMode,
                unpackMode, hashMode, copyOut, copyIn, auditMode, validateMode, tree, extract, checksumFlagMode,
                filetimeMode, setFiletimeMode, sectorLayoutMode, rangesMode, isOptimizedMode,
                optind, args.Length, createList, ref rewrite, ref packIsoFile, ref path) != 0)
        {
            return 1;
        }

        // --layout is a create modifier: it needs a directory source so every
        // entry has a host file to read from. Rewrite/extract/list modes reject it.
        if (layoutFile != null && createList.Count == 0)
        {
            Logger.LogErr("Error: --layout is only used with -c <directory> or --pack <directory>\n");
            return 1;
        }

        if (checksumFlagMode && (info || lsMode || xexInfoMode || xbeInfoMode || repairMode || salvageMode || tree ||
                                 hashMode || copyOut || copyIn || auditMode ||
                                 validateMode || unpackMode || createList.Count > 0 || rewrite || filetimeMode ||
                                 setFiletimeMode || sectorLayoutMode || rangesMode || isOptimizedMode))
        {
            Logger.LogErr("Error: --checksum cannot be combined with other modes\n");
            return 1;
        }

        // Item #32: order-independent --silent validation.
        if (silentFlag)
        {
            if (!checksumFlagMode)
            {
                Logger.LogErr("Error: --silent requires --checksum\n");
                PrintUsage();
                return 1;
            }

            checksumSilent = true;
        }

        // Item #33: --file-time is only consumed by create mode (and --pack /
        // build-image, which translate to it); it used to be silently accepted
        // and ignored by extract/list/tree/rewrite.
        if (createFileTime.HasValue && createList.Count == 0)
        {
            Logger.LogErr(
                "Error: --file-time is only used with -c (create mode), --pack <directory>, or build-image\n");
            return 1;
        }

        // Item #34: --preserve-attrs is only consumed by rewrite; extract/list/
        // tree and the redump modes used to accept and ignore it.
        if (preserveAttrs && !rewrite)
        {
            Logger.LogErr("Error: --preserve-attrs is only used with -r (rewrite)\n");
            return 1;
        }

        // Item #37: --jobs/--policy are only consumed by the parallel ZAR path
        // (--compress expands to --zar later, so accept it here too).
        if (!zarMode && !compressAlias && (jobs != 1 || zarPolicy != null))
        {
            Logger.LogErr("Error: --jobs/--policy are only used with --zar\n");
            return 1;
        }

        if (filetimeMode && (info || lsMode || xexInfoMode || xbeInfoMode || repairMode || salvageMode || tree ||
                             hashMode || copyOut || copyIn || auditMode ||
                             validateMode || unpackMode || createList.Count > 0 || rewrite || checksumFlagMode ||
                             setFiletimeMode || sectorLayoutMode || rangesMode || isOptimizedMode))
        {
            Logger.LogErr("Error: --filetime cannot be combined with other modes\n");
            return 1;
        }

        if (setFiletimeMode && (info || lsMode || xexInfoMode || xbeInfoMode || repairMode || salvageMode || tree ||
                                hashMode || copyOut || copyIn || auditMode ||
                                validateMode || unpackMode || createList.Count > 0 || rewrite || checksumFlagMode ||
                                filetimeMode || sectorLayoutMode || rangesMode || isOptimizedMode))
        {
            Logger.LogErr("Error: --set-filetime cannot be combined with other modes\n");
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

        // CLI-013: only the rebuild verb consumes --security-sectors; the
        // redump batch entry point explicitly discards it. Reject it loudly
        // everywhere the main parser accepts it instead of ignoring it.
        if (securitySectorsPath != null)
        {
            Logger.LogErr("Error: --security-sectors/--sectors is only supported by the rebuild verb\n");
            return 1;
        }

        if ((skipSectors.HasValue || prependSectors.HasValue) &&
            (info || lsMode || xexInfoMode || xbeInfoMode || repairMode || salvageMode || hashMode || copyOut ||
             copyIn || auditMode || validateMode || validateFlag || sectorLayoutMode || rangesMode ||
             // CLI-015: --skip-sectors is also ignored by the redump batch
             // (RunRedumpBatch takes no offset) and by --checksum
             // (ComputeImageChecksum takes no offset). --filetime/--set-filetime
             // consume skipSectors, so they stay allowed.
             videoMode || randomMode || seedMode || wipeMode || trimMode || petrifyMode || updateMode || zarMode ||
             allMode || bestMode || compressAlias || checksumFlagMode))
        {
            Logger.LogErr(
                "Error: --skip-sectors/--prepend-sectors are only supported in extract, list, tree, rewrite (-r), unpack, filetime, set-filetime, and --is-optimized modes; --prepend-sectors is also valid with create (-c)\n");
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

        if (batchDir != null && (createList.Count > 0 || info || lsMode || xexInfoMode || xbeInfoMode || repairMode ||
                                 salvageMode || unpackMode || hashMode ||
                                 copyOut || copyIn || validateMode || checksumFlagMode || filetimeMode ||
                                 setFiletimeMode || sectorLayoutMode || rangesMode || isOptimizedMode))
        {
            Logger.LogErr(
                "Error: --batch is only supported in extract, list, tree, rewrite (-r), and audit (-V) modes\n");
            return 1;
        }

        // #13 (xdvdfs #190): resume only makes sense where files are unpacked.
        // `extract` is still true for rewrite (-r) and create (-c), so exclude those.
        if (skipExisting && !((extract && !rewrite && createList.Count == 0) || unpackMode || copyOut))
        {
            Logger.LogErr(
                "Error: --skip-existing is only supported in extract (-x), --unpack, and --copy-out modes\n");
            return 1;
        }

        // #9 (xdvdfs #187): per-file robustness only makes sense where files are unpacked.
        if (continueOnError && !((extract && !rewrite && createList.Count == 0) || unpackMode || copyOut))
        {
            Logger.LogErr(
                "Error: --continue-on-error is only supported in extract (-x), --unpack, and --copy-out modes\n");
            return 1;
        }

        if (copyInNoBackup && !copyIn && !repairMode)
        {
            Logger.LogErr("Error: --no-backup requires --copy-in or --repair\n");
            return 1;
        }

        if (repairDryRun && !repairMode)
        {
            Logger.LogErr("Error: --dry-run requires --repair\n");
            return 1;
        }

        if (repairOut != null && !salvageMode)
        {
            Logger.LogErr("Error: --repair-out requires --salvage\n");
            return 1;
        }

        if (unpackMode && (info || lsMode || xexInfoMode || xbeInfoMode || repairMode || salvageMode || tree ||
                           hashMode || copyOut || copyIn || auditMode || validateMode ||
                           checksumFlagMode || filetimeMode || setFiletimeMode || sectorLayoutMode || rangesMode ||
                           isOptimizedMode))
        {
            Logger.LogErr("Error: --unpack cannot be combined with other modes\n");
            return 1;
        }

        // CLI-001: the classic operational modes stack silently (last flag wins);
        // require exactly one. (--md5/--sha256 double-selection is rejected
        // during parsing since both share hashMode.)
        int classicModes = new[]
        {
            listMode, tree, info, lsMode, xexInfoMode, xbeInfoMode, repairMode, salvageMode, hashMode, copyOut,
            copyIn, auditMode, validateMode, sectorLayoutMode, rangesMode, isOptimizedMode
        }.Count(b => b);
        if (classicModes > 1)
        {
            Logger.LogErr(
                "Error: -l/-t/-i/--ls/--xex-info/--xbe-info/--repair/--salvage/--md5/--sha256/-V/validate/--copy-out/--copy-in/--sector-layout/--ranges/--is-optimized cannot be combined with other modes\n");
            return 1;
        }

        // XboxKit redump modes are mutually exclusive with other operational modes
        bool anyRedumpMode = videoMode || randomMode || seedMode || wipeMode || trimMode || petrifyMode || updateMode ||
                             zarMode || allMode || bestMode || compressAlias;
        if (anyRedumpMode && (info || lsMode || xexInfoMode || xbeInfoMode || repairMode || salvageMode || tree ||
                              hashMode || copyOut || copyIn || auditMode ||
                              validateMode || unpackMode || createList.Count > 0 || rewrite || checksumFlagMode ||
                              filetimeMode || setFiletimeMode || sectorLayoutMode || rangesMode || isOptimizedMode))
        {
            Logger.LogErr(
                "Error: --video/--random/--seed/--wipe/--trim/--petrify/--update/--zar/--all/--best/--compress/rebuild cannot be combined with other modes\n");
            return 1;
        }

        if ((filetimeMode || setFiletimeMode) && (anyRedumpMode || batchDir != null))
        {
            Logger.LogErr("Error: --filetime/--set-filetime cannot be combined with redump or --batch modes\n");
            return 1;
        }

        if (anyRedumpMode && batchDir != null)
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

        // Item 37 follow-up: --jobs only takes effect for a lone --zar over
        // several inputs (the parallel branch). --compress/multi-mode used to
        // accept it and silently run serially.
        int redumpModeCount = new[]
        {
            videoMode, randomMode, seedMode, wipeMode, trimMode, petrifyMode, updateMode, zarMode
        }.Count(b => b);
        if (jobs != 1 && !(zarMode && redumpModeCount == 1))
        {
            Logger.LogErr("Error: --jobs is only used with a lone --zar over several inputs\n");
            return 1;
        }

        // CLI-016: the -y/-n conflict must fire before the early-return
        // dispatches below (--filetime/--set-filetime/--checksum), which
        // previously swallowed it.
        if (assumeYes && assumeNo)
        {
            Logger.LogErr("[ERROR] Cannot use both --no (-n) and --yes (-y)\n");
            return 1;
        }

        // CLI-003: --validate* is only consumed by -r (rewrite) and the
        // validate verb. Anywhere else it was silently ignored.
        if ((validateFlag || validateChecksums || validateStrict || validateReport != null) &&
            !validateMode && !rewrite)
        {
            Logger.LogErr(
                "Error: --validate/--validate-checksums/--validate-strict/--validate-report require -r (rewrite) or validate mode\n");
            return 1;
        }

        // --filetime / --set-filetime dispatch before batch expansion (positional value handling).
        if (filetimeMode)
        {
            // CLI-012: exact arity. CLI-017: -d/-o/-D are ignored here.
            if (RejectIgnoredOutputFlags("--filetime"))
                return 1;

            if (optind >= args.Length)
            {
                Logger.LogErr("Error: --filetime requires <iso>\n");
                PrintUsage();
                return 1;
            }

            if (optind + 1 < args.Length)
            {
                Logger.LogErr("Error: --filetime takes exactly one <iso> (extra arguments not allowed)\n");
                PrintUsage();
                return 1;
            }

            string isoPath = args[optind];
            try
            {
                ulong raw = XisoReader.GetFileTimeRaw(isoPath, skipSectors);
                DateTimeOffset dto = FileTimeHelper.FromFileTimeRaw(raw);
                string iso8601 = dto.ToString("O", CultureInfo.InvariantCulture);
                Logger.Log($"FileTime: {iso8601} ({raw}) 0x{raw:X16}\n");
                if (jsonMode)
                {
                    WriteJson(new JsonFileTime(1, "filetime", isoPath, iso8601, raw, $"0x{raw:X16}"));
                }

                // Also emit raw only to stdout for scripting when quiet? Match xdvdfs raw behavior on --silent?
                return 0;
            }
            catch (Exception ex) when (ex is XisoFormatException or IOException or FileNotFoundException
                                           or DirectoryNotFoundException)
            {
                Logger.LogErr($"Error reading filetime from {isoPath}: {ex.Message}\n");
                return 1;
            }
        }

        if (setFiletimeMode)
        {
            // CLI-012: exact arity. CLI-017: -d/-o/-D are ignored here.
            if (RejectIgnoredOutputFlags("--set-filetime"))
                return 1;

            if (optind + 1 >= args.Length)
            {
                Logger.LogErr(
                    "Error: --set-filetime requires <iso> <value> (value: ISO-8601, decimal raw, 0x hex, or 'now')\n");
                PrintUsage();
                return 1;
            }

            if (optind + 2 < args.Length)
            {
                Logger.LogErr("Error: --set-filetime takes exactly <iso> <value>\n");
                PrintUsage();
                return 1;
            }

            string isoPath = args[optind];
            string valueStr = args[optind + 1];
            if (!FileTimeHelper.TryParseFileTime(valueStr, out ulong raw, out DateTimeOffset dto))
            {
                Logger.LogErr(
                    $"Error: invalid filetime value '{valueStr}' (expected ISO-8601, decimal, 0x hex, 'now', or '0')\n");
                return 1;
            }

            try
            {
                XisoReader.SetFileTime(isoPath, raw, skipSectors);
                string iso8601 = dto.ToString("O", CultureInfo.InvariantCulture);
                Logger.Log($"Set FileTime for {isoPath} to {iso8601} ({raw}) 0x{raw:X16}\n");
                return 0;
            }
            catch (Exception ex) when (ex is XisoFormatException or IOException or FileNotFoundException
                                           or UnauthorizedAccessException)
            {
                Logger.LogErr($"Error setting filetime for {isoPath}: {ex.Message}\n");
                return 1;
            }
        }

        if (sectorLayoutMode)
        {
            // CLI-012: exactly one <iso>. CLI-017: -d/-o/-D ignored.
            if (RejectIgnoredOutputFlags("--sector-layout"))
                return 1;

            if (optind >= args.Length)
            {
                Logger.LogErr("Error: --sector-layout requires <iso>\n");
                PrintUsage();
                return 1;
            }

            if (optind + 1 < args.Length)
            {
                Logger.LogErr("Error: --sector-layout takes exactly one <iso> (extra arguments not allowed)\n");
                PrintUsage();
                return 1;
            }

            string isoPath = args[optind];
            try
            {
                SectorLayout layout = XisoReader.GetSectorLayout(isoPath);
                VolumeInfo volume = layout.Volume;
                int fileCount = 0;
                int dirCount = 0;
                foreach (FileSectorExtent entry in layout.Entries)
                {
                    if (entry.IsDirectory)
                        dirCount++;
                    else
                        fileCount++;
                }

                Logger.Log($"Sector layout: {isoPath}\n");
                Logger.Log($"  Format:         {volume.DiscFormat}\n");
                Logger.Log($"  Root dir:       sector {volume.RootDirSector} ({volume.RootDirSize} bytes)\n");
                Logger.Log($"  Total sectors:  {volume.TotalSectors} ({volume.FileLength} bytes)\n");
                Logger.Log($"  Entries:        {fileCount} file(s), {dirCount} dir(s)\n");
                foreach (FileSectorExtent entry in layout.Entries)
                {
                    string kind = entry.IsDirectory ? "dir " : "file";
                    Logger.Log(
                        $"    [{kind}] {entry.Path}  sector {entry.StartSector} (+{entry.SectorCount})  {entry.FileSize} bytes\n");
                }

                Logger.Log($"  Used ranges:    {layout.UsedRanges.Count}\n");
                foreach (SectorRange range in layout.UsedRanges)
                {
                    Logger.Log($"    {range.StartSector}-{range.StartSector + range.SectorCount - 1}\n");
                }

                Logger.Log($"  Free ranges:    {layout.FreeRanges.Count}\n");
                foreach (SectorRange range in layout.FreeRanges)
                {
                    Logger.Log($"    {range.StartSector}-{range.StartSector + range.SectorCount - 1}\n");
                }

                if (jsonMode)
                {
                    JsonSectorLayout payload = new(
                        1,
                        "sector-layout",
                        isoPath,
                        ToJsonVolume(volume, null, volume.FileTimeRaw),
                        fileCount,
                        dirCount,
                        layout.Entries.Select(static e => new JsonExtent(
                            e.Path,
                            e.IsDirectory,
                            e.StartSector,
                            e.SectorCount,
                            e.FileSize)).ToList(),
                        layout.UsedRanges.Select(static r => new JsonRange(r.StartSector, r.SectorCount)).ToList(),
                        layout.FreeRanges.Select(static r => new JsonRange(r.StartSector, r.SectorCount)).ToList());
                    WriteJson(payload);
                }

                return 0;
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException or ExtractErrorException)
            {
                Logger.LogErr($"Error reading sector layout from {isoPath}: {ex.Message}\n");
                return 1;
            }
        }

        if (rangesMode)
        {
            // CLI-012: exactly one <iso>. CLI-017: -d/-o/-D ignored.
            if (RejectIgnoredOutputFlags("--ranges"))
                return 1;

            if (optind >= args.Length)
            {
                Logger.LogErr("Error: --ranges requires <iso>\n");
                PrintUsage();
                return 1;
            }

            if (optind + 1 < args.Length)
            {
                Logger.LogErr("Error: --ranges takes exactly one <iso> (extra arguments not allowed)\n");
                PrintUsage();
                return 1;
            }

            string isoPath = args[optind];
            try
            {
                (List<(uint Start, uint End)> sys, List<(uint Start, uint End)> files) =
                    XisoRanges.GetXisoRanges(isoPath, quiet: Logger.Quiet);
                Logger.Log($"Sector ranges: {isoPath}\n");
                Logger.Log($"  System ({sys.Count} range(s)):\n");
                foreach ((uint start, uint end) in sys)
                {
                    Logger.Log($"    {start}-{end}\n");
                }

                Logger.Log($"  Files ({files.Count} range(s)):\n");
                foreach ((uint start, uint end) in files)
                {
                    Logger.Log($"    {start}-{end}\n");
                }

                if (jsonMode)
                {
                    JsonRanges payload = new(
                        1,
                        "ranges",
                        isoPath,
                        sys.Select(static r => new JsonRange(r.Start, r.End - r.Start + 1)).ToList(),
                        files.Select(static r => new JsonRange(r.Start, r.End - r.Start + 1)).ToList());
                    WriteJson(payload);
                }

                return 0;
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException or ExtractErrorException)
            {
                Logger.LogErr($"Error reading sector ranges from {isoPath}: {ex.Message}\n");
                return 1;
            }
        }

        if (isOptimizedMode)
        {
            // CLI-012: exactly one <iso>. CLI-017: -d/-o/-D ignored.
            if (RejectIgnoredOutputFlags("--is-optimized"))
                return 1;

            if (optind >= args.Length)
            {
                Logger.LogErr("Error: --is-optimized requires <iso>\n");
                PrintUsage();
                return 1;
            }

            if (optind + 1 < args.Length)
            {
                Logger.LogErr("Error: --is-optimized takes exactly one <iso> (extra arguments not allowed)\n");
                PrintUsage();
                return 1;
            }

            string isoPath = args[optind];
            try
            {
                bool optimized = XisoReader.IsOptimizedImage(isoPath, skipSectors);
                Logger.Log($"{isoPath}: {(optimized ? "optimized" : "not optimized")}\n");
                return 0;
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException)
            {
                Logger.LogErr($"Error probing {isoPath}: {ex.Message}\n");
                return 1;
            }
        }

        if (checksumFlagMode)
        {
            // CLI-017: -d/-o/-D are ignored here.
            if (RejectIgnoredOutputFlags("--checksum"))
                return 1;

            if (optind >= args.Length)
            {
                Logger.LogErr("Error: --checksum requires <iso>\n");
                PrintUsage();
                return 1;
            }

            int cExit = 0;
            List<JsonChecksum>? jsonChecksums = jsonMode ? [] : null;
            for (int k = optind; k < args.Length; k++)
            {
                string iso = args[k];
                try
                {
                    byte[] hash = XisoChecksum.ComputeImageChecksum(iso);
                    string hex = Convert.ToHexString(hash).ToLowerInvariant();
                    if (checksumSilent)
                        Logger.Log($"{hex}\n");
                    else
                        Logger.Log($"{hex}\t{iso}\n");
                    jsonChecksums?.Add(new JsonChecksum(iso, "sha3-256", hex));
                }
                catch (Exception ex)
                {
                    Logger.LogErr($"Error checksumming {iso}: {ex.Message}\n");
                    cExit = 1;
                }
            }

            if (jsonChecksums != null)
            {
                WriteJson(new JsonChecksums(1, "checksum", jsonChecksums));
            }

            return cExit;
        }

        // The list of ISO files to process: explicit filenames, a --batch directory scan,
        // or a --pack ISO input.
        List<string>? isoFiles = ExpandIsoFiles(batchDir, batchRecursive, args, optind, packIsoFile);
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

        // CLI-010: a shared -o across a rewrite batch would clobber every input
        // into the same file (mirrors the RunRedumpBatch single-output guard).
        if (rewrite && outputName != null && isoFiles.Count != 1)
        {
            Logger.LogErr("Error: -o <output> can only be used with a single input file\n");
            return 1;
        }

        Logger.Log(Constants.Banner);

        // Dispatch XboxKit redump modes (batch) — after expansion so --batch handling is uniform,
        // but rebuild already returned above.
        if (videoMode || randomMode || seedMode || wipeMode || trimMode || petrifyMode || updateMode || zarMode)
        {
            // CLI-017: RunRedumpBatch consumes -o but ignores -d/-D.
            if (path != null)
            {
                Logger.LogErr("Error: -d <dir> is not used with redump modes\n");
                return 1;
            }

            if (deleteOld)
            {
                Logger.LogErr("Error: -D is only used with -r (rewrite)\n");
                return 1;
            }

            return RunRedumpBatch(isoFiles, videoMode, randomMode, seedMode, wipeMode, trimMode, petrifyMode,
                updateMode, zarMode, outputName, assumeYes, assumeNo, jobs, zarPolicy);
        }

        if (createList.Count > 0)
        {
            // CLI-017: create carries its output in the -c name slot; -d/-o/-D are ignored.
            if (RejectIgnoredOutputFlags("-c (create)"))
                return 1;

            XisoLayoutSpec? layout = null;
            if (layoutFile != null)
            {
                try
                {
                    layout = XisoLayoutSpec.Load(layoutFile);
                }
                catch (Exception ex) when (ex is IOException or FormatException or UnauthorizedAccessException or ArgumentException)
                {
                    Logger.LogErr($"Error: cannot read sector layout {layoutFile}: {ex.Message}\n");
                    return 1;
                }

                Logger.Log(
                    $"using sector layout {layoutFile} ({layout.Entries.Count} fixed extent(s)" +
                    (layout.TotalSectors is long total ? $", {total} total sector(s)" : "") + ")\n");
            }

            foreach ((string dir, string? name) in createList)
            {
                string? outputDir = null;
                string? isoName = null;

                if (name != null)
                {
                    int lastSep = name.LastIndexOf(Constants.PathChar);
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
                        excludePatterns: excludePatterns.Count > 0 ? excludePatterns : null,
                        fileTime: createFileTime,
                        layout: layout);
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
            // CLI-012: at most <iso> [internal-path]. CLI-017: -d/-o/-D ignored.
            if (RejectIgnoredOutputFlags("-i"))
                return 1;

            if (optind >= args.Length)
            {
                PrintUsage();
                return 1;
            }

            if (optind + 2 < args.Length)
            {
                Logger.LogErr("Error: -i takes at most <iso> [internal-path] (extra arguments not allowed)\n");
                return 1;
            }

            string xisoPath = args[optind];
            string internalPath = optind + 1 < args.Length ? args[optind + 1] : "/";

            try
            {
                VolumeInfo volInfo = XisoReader.GetVolumeInfo(xisoPath, skipSectors);

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
                Logger.Log($"  Disc Format:    {volInfo.DiscFormat}\n");
                Logger.Log($"  Root Sector:    {volInfo.RootDirSector}\n");
                Logger.Log($"  Root Size:      {volInfo.RootDirSize} bytes\n");
                try
                {
                    ulong raw = XisoReader.GetFileTimeRaw(xisoPath, skipSectors);
                    DateTimeOffset dto = FileTimeHelper.FromFileTimeRaw(raw);
                    string iso8601 = dto.ToString("O", CultureInfo.InvariantCulture);
                    Logger.Log($"  FileTime:       {iso8601} ({raw})\n");
                    Logger.Log($"  FileTime raw:   0x{raw:X16} ({raw})\n");
                }
                catch (Exception ex) when (ex is XisoFormatException or IOException)
                {
                    Logger.Log($"  FileTime:       (unavailable: {ex.Message})\n");
                }

                Logger.Log("\n");

                IReadOnlyList<EntryInfo> entries = XisoReader.ListDirectory(xisoPath, internalPath, skipSectors);
                if (entries.Count == 0)
                {
                    Logger.Log($"{internalPath}: empty directory\n");
                }
                else
                {
                    Logger.Log($"Directory: {internalPath}\n\n");
                    foreach (EntryInfo entry in entries)
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

                if (jsonMode)
                {
                    string? fileTimeIso = null;
                    ulong fileTimeRaw = 0;
                    try
                    {
                        fileTimeRaw = XisoReader.GetFileTimeRaw(xisoPath, skipSectors);
                        fileTimeIso = FileTimeHelper.FromFileTimeRaw(fileTimeRaw)
                            .ToString("O", CultureInfo.InvariantCulture);
                    }
                    catch (Exception ex) when (ex is XisoFormatException or IOException)
                    {
                        // JSON keeps fileTime null when the descriptor tail is unreadable.
                    }

                    JsonInfo payload = new(
                        1,
                        "info",
                        xisoPath,
                        internalPath,
                        ToJsonVolume(volInfo, fileTimeIso, fileTimeRaw),
                        entries.Select(static e => new JsonInfoEntry(
                            e.Name,
                            e.IsDirectory,
                            e.StartSector,
                            e.FileSize,
                            e.Attributes,
                            e.LeftChildOffset,
                            e.RightChildOffset)).ToList());
                    WriteJson(payload);
                    return 0;
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
            // CLI-012: at most <iso> [internal-path]. CLI-017: -d/-o/-D ignored.
            if (RejectIgnoredOutputFlags("--ls"))
                return 1;

            if (optind >= args.Length)
            {
                PrintUsage();
                return 1;
            }

            if (optind + 2 < args.Length)
            {
                Logger.LogErr("Error: --ls takes at most <iso> [internal-path] (extra arguments not allowed)\n");
                return 1;
            }

            string xisoPath = args[optind];
            string internalPath = optind + 1 < args.Length ? args[optind + 1] : "/";

            try
            {
                IReadOnlyList<string> entries = XisoReader.ListDirectoryFlat(xisoPath, internalPath);
                if (entries.Count == 0)
                {
                    Logger.Log($"{internalPath}: empty directory\n");
                }
                else
                {
                    foreach (string name in entries)
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
            // CLI-012: exactly <iso> <internal-path>. CLI-017: -d/-o/-D ignored.
            if (RejectIgnoredOutputFlags("--xex-info"))
                return 1;

            if (optind + 1 >= args.Length)
            {
                PrintUsage();
                return 1;
            }

            if (optind + 2 < args.Length)
            {
                Logger.LogErr("Error: --xex-info takes exactly <iso> <internal-path>\n");
                return 1;
            }

            string xisoPath = args[optind];
            string internalPath = args[optind + 1];

            try
            {
                XexInfo? xex = XisoReader.GetXexInfo(xisoPath, internalPath);
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

                if (jsonMode)
                {
                    WriteJson(new JsonXexInfo(
                        1,
                        "xex-info",
                        xisoPath,
                        internalPath,
                        xex.ModuleFlags,
                        xex.HeaderSize,
                        xex.EntryPoint,
                        xex.ImageBaseAddress,
                        xex.ImageSize,
                        xex.LoadAddress,
                        xex.Region,
                        xex.AllowedMediaTypes,
                        xex.MediaId,
                        xex.TitleId,
                        xex.Version,
                        xex.Platform,
                        xex.DiscNumber,
                        xex.DiscCount,
                        xex.EncryptionType,
                        xex.CompressionType));
                }
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException)
            {
                Logger.LogErr($"Error: {ex.Message}\n");
                return 1;
            }

            return 0;
        }

        if (xbeInfoMode)
        {
            // CLI-012: exactly <iso> <internal-path>. CLI-017: -d/-o/-D ignored.
            if (RejectIgnoredOutputFlags("--xbe-info"))
                return 1;

            if (optind + 1 >= args.Length)
            {
                PrintUsage();
                return 1;
            }

            if (optind + 2 < args.Length)
            {
                Logger.LogErr("Error: --xbe-info takes exactly <iso> <internal-path>\n");
                return 1;
            }

            string xisoPath = args[optind];
            string internalPath = args[optind + 1];

            try
            {
                XbeInfo? xbe = XisoReader.GetXbeInfo(xisoPath, internalPath);
                if (xbe == null)
                {
                    Logger.LogErr($"Not an XBEH executable: {internalPath}\n");
                    return 1;
                }

                Logger.Log($"XBE info: {internalPath}\n");
                Logger.Log($"  Base address:    0x{xbe.BaseAddress:X8}\n");
                Logger.Log($"  Entry point:     0x{xbe.EntryPoint:X8}\n");
                Logger.Log($"  Sections:        {xbe.SectionCount}\n");
                Logger.Log($"  Init flags:      0x{xbe.InitFlags:X8}\n");
                Logger.Log($"  Cert size:       {xbe.CertSize} bytes\n");
                Logger.Log($"  Cert timestamp:  0x{xbe.CertTimeDate:X8}\n");
                Logger.Log($"  Title ID:        0x{xbe.TitleId:X8}\n");
                Logger.Log($"  Title name:      {xbe.TitleName}\n");
                Logger.Log($"  Media:           0x{xbe.AllowedMedia:X8}{FormatXbeMedia(xbe.AllowedMedia)}\n");
                Logger.Log($"  Region:          0x{xbe.GameRegion:X8}{FormatXbeRegion(xbe.GameRegion)}\n");
                Logger.Log($"  Ratings:         0x{xbe.GameRatings:X8}\n");
                Logger.Log($"  Disc:            {xbe.DiskNumber}\n");
                Logger.Log($"  Version:         0x{xbe.Version:X8}\n");

                if (jsonMode)
                {
                    WriteJson(new JsonXbeInfo(
                        1,
                        "xbe-info",
                        xisoPath,
                        internalPath,
                        xbe.BaseAddress,
                        xbe.EntryPoint,
                        xbe.SectionCount,
                        xbe.InitFlags,
                        xbe.CertSize,
                        xbe.CertTimeDate,
                        xbe.TitleId,
                        xbe.TitleName,
                        xbe.AlternateTitleIds,
                        xbe.AllowedMedia,
                        xbe.GameRegion,
                        xbe.GameRatings,
                        xbe.DiskNumber,
                        xbe.Version));
                }
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException)
            {
                Logger.LogErr($"Error: {ex.Message}\n");
                return 1;
            }

            return 0;
        }

        if (repairMode)
        {
            // CLI-017: -d/-o/-D are ignored here (extras already rejected below).
            if (RejectIgnoredOutputFlags("--repair"))
                return 1;

            if (optind >= args.Length || optind + 1 < args.Length)
            {
                PrintUsage();
                return 1;
            }

            string xisoPath = args[optind];
            string backupPath = xisoPath + ".old";
            bool hadBackup = File.Exists(backupPath);
            try
            {
                RepairResult result = XisoReader.Repair(xisoPath, createBackup: !copyInNoBackup, dryRun: repairDryRun);
                Logger.Log($"Repairing {xisoPath}:\n\n");
                if (result.BackupPath != null)
                {
                    Logger.Log(hadBackup
                        ? $"Backup of pre-repair image replaced at {backupPath}\n"
                        : $"Backup of pre-repair image written to {backupPath}\n");
                }

                if (result.Fixed.Count > 0)
                {
                    Logger.Log(repairDryRun ? "  Would fix:\n" : "  Fixed:\n");
                    foreach (string fix in result.Fixed)
                    {
                        Logger.Log($"    - {fix}\n");
                    }
                }

                if (result.Remaining.Count == 0)
                {
                    Logger.Log("  Result:         PASS\n");
                }
                else
                {
                    Logger.Log($"  Result:         FAIL ({result.Remaining.Count} issue(s))\n");
                    foreach (string issue in result.Remaining)
                    {
                        Logger.LogErr($"    - {issue}\n");
                    }

                    return 1;
                }

                Logger.Log("\n");
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
            {
                Logger.LogErr($"Error: {ex.Message}\n");
                return 1;
            }

            return 0;
        }

        if (salvageMode)
        {
            // CLI-017: -d/-o/-D are ignored here (--repair-out carries the output).
            if (RejectIgnoredOutputFlags("--salvage"))
                return 1;

            if (optind >= args.Length || optind + 1 < args.Length)
            {
                PrintUsage();
                return 1;
            }

            string xisoPath = args[optind];
            string salvageOutput = repairOut ?? XisoSalvager.DefaultOutputPath(xisoPath);
            if (!OverwritePrompt.ConfirmOverwrite(salvageOutput, assumeYes, assumeNo))
                return 1;

            try
            {
                SalvageResult result = XisoReader.Salvage(xisoPath, salvageOutput);
                Logger.Log($"Salvaging {xisoPath} -> {result.OutputPath}:\n\n");
                if (result.Copied.Count > 0)
                {
                    Logger.Log($"  Carried ({result.Copied.Count}):\n");
                    foreach (string carried in result.Copied)
                    {
                        Logger.Log($"    + {carried}\n");
                    }
                }

                if (result.Skipped.Count > 0)
                {
                    Logger.Log($"  Dropped ({result.Skipped.Count}):\n");
                    foreach (string dropped in result.Skipped)
                    {
                        Logger.LogErr($"    - {dropped}\n");
                    }
                }

                if (result.OutputIssues.Count == 0)
                {
                    Logger.Log("  Result:         PASS\n");
                }
                else
                {
                    Logger.Log(
                        $"  Result:         FAIL ({result.OutputIssues.Count} issue(s) in rebuilt image)\n");
                    foreach (string issue in result.OutputIssues)
                    {
                        Logger.LogErr($"    - {issue}\n");
                    }

                    return 1;
                }

                Logger.Log("\n");
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
            {
                Logger.LogErr($"Error: {ex.Message}\n");
                return 1;
            }

            return 0;
        }

        // #13 (xdvdfs #190): resume an interrupted unpack by skipping files already
        // on disk with matching sizes. Pairs with --batch: each image in the bulk
        // run resumes instead of redoing completed files.
        // #9 (xdvdfs #187): with --continue-on-error, per-file failures are logged
        // and skipped instead of aborting the run; the summary still fails the exit code.
        UnpackOptions? unpackOptions = skipExisting || continueOnError
            ? new UnpackOptions { SkipExisting = skipExisting, ContinueOnError = continueOnError }
            : null;

        if (unpackMode)
        {
            // CLI-017: --unpack takes its destination positionally; -o/-D are ignored.
            if (RejectIgnoredOutputFlags("--unpack"))
                return 1;

            return RunUnpackMode(args, optind, path, skipSectors, skipExisting, continueOnError);
        }

        if (hashMode)
        {
            // CLI-012: at most <iso> [internal-path]. CLI-017: -d/-o/-D ignored.
            if (RejectIgnoredOutputFlags("--md5/--sha256"))
                return 1;

            if (optind >= args.Length)
            {
                PrintUsage();
                return 1;
            }

            if (optind + 2 < args.Length)
            {
                Logger.LogErr("Error: --md5/--sha256 takes at most <iso> [internal-path]\n");
                return 1;
            }

            string xisoPath = args[optind];
            string? internalPath = optind + 1 < args.Length ? args[optind + 1] : null;
            HashAlgorithmName algorithm = new(hashAlgo);

            try
            {
                if (internalPath != null)
                {
                    // Hash specific file or all files in a directory
                    EntryInfo? entry = XisoReader.GetEntryInfo(xisoPath, internalPath);
                    if (entry == null)
                    {
                        Logger.LogErr($"Path not found: {internalPath}\n");
                        return 1;
                    }

                    if (entry.IsDirectory)
                    {
                        IReadOnlyList<(string Path, byte[] Hash)> results =
                            XisoReader.ComputeDirectoryHashes(xisoPath, internalPath, algorithm);
                        foreach ((string filePath, byte[] hash) in results)
                        {
                            Logger.Log($"{Convert.ToHexString(hash).ToLowerInvariant()}  {filePath}\n");
                        }
                    }
                    else
                    {
                        byte[]? hash = XisoReader.ComputeFileHash(xisoPath, internalPath, algorithm);
                        if (hash != null)
                            Logger.Log($"{Convert.ToHexString(hash).ToLowerInvariant()}  {internalPath}\n");
                    }
                }
                else
                {
                    // Hash all files
                    IReadOnlyList<(string Path, byte[] Hash)> results =
                        XisoReader.ComputeDirectoryHashes(xisoPath, "/", algorithm);
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
            // CLI-012: exactly 3 positionals. CLI-017: -d/-o/-D are ignored here.
            if (RejectIgnoredOutputFlags("--copy-out"))
                return 1;

            if (optind + 2 >= args.Length)
            {
                PrintUsage();
                return 1;
            }

            if (optind + 3 < args.Length)
            {
                Logger.LogErr("Error: --copy-out takes exactly <iso> <internal-path> <dest>\n");
                return 1;
            }

            string xisoPath = args[optind];
            string internalPath = args[optind + 1];
            string destPath = args[optind + 2];

            try
            {
                EntryInfo? entry = XisoReader.GetEntryInfo(xisoPath, internalPath);
                if (entry == null)
                {
                    Logger.LogErr($"Path not found in XISO: {internalPath}\n");
                    return 1;
                }

                // #13 (xdvdfs #190): with --skip-existing, an up-to-date destination
                // is reported as skipped instead of being recopied.
                if (!entry.IsDirectory && unpackOptions?.ShouldSkip(destPath, entry.FileSize) == true)
                {
                    Logger.Log($"skip: {destPath} ({entry.FileSize} bytes)\n");
                    return 0;
                }

                XisoReader.CopyOut(xisoPath, internalPath, destPath, unpackOptions);
                Logger.Log($"Copied {internalPath} to {destPath}\n");
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException
                                           or ExtractErrorException)
            {
                Logger.LogErr($"Error: {ex.Message}\n");
                return 1;
            }

            return 0;
        }

        if (copyIn)
        {
            // CLI-012: exactly 3 positionals. CLI-017: -d/-o/-D are ignored here.
            if (RejectIgnoredOutputFlags("--copy-in"))
                return 1;

            if (optind + 2 >= args.Length)
            {
                PrintUsage();
                return 1;
            }

            if (optind + 3 < args.Length)
            {
                Logger.LogErr("Error: --copy-in takes exactly <iso> <host-file> <internal-path>\n");
                return 1;
            }

            string xisoPath = args[optind];
            string hostPath = args[optind + 1];
            string internalPath = args[optind + 2];
            if (!File.Exists(hostPath))
            {
                Logger.LogErr($"Host file not found: {hostPath}\n");
                return 1;
            }

            if (XisoPaths.AreSamePath(xisoPath, hostPath))
            {
                Logger.LogErr($"Error: host file {hostPath} is the image itself; choose another file\n");
                return 1;
            }

            string backupPath = xisoPath + ".old";
            bool hadBackup = File.Exists(backupPath);
            try
            {
                XisoReader.CopyIn(xisoPath, hostPath, internalPath, createBackup: !copyInNoBackup);
                if (!copyInNoBackup)
                {
                    Logger.Log(hadBackup
                        ? $"Backup of pre-patch image replaced at {backupPath}\n"
                        : $"Backup of pre-patch image written to {backupPath}\n");
                }

                Logger.Log($"Copied {hostPath} into {xisoPath}:{internalPath}\n");
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException
                                           or ExtractErrorException)
            {
                Logger.LogErr($"Error: {ex.Message}\n");
                return 1;
            }

            return 0;
        }

        if (auditMode)
        {
            // CLI-017: -d/-o/-D are ignored here.
            if (RejectIgnoredOutputFlags("-V"))
                return 1;

            if (isoFiles.Count == 0)
            {
                PrintUsage();
                return 1;
            }

            bool allValid = true;
            for (int i = 0; i < isoFiles.Count; i++)
            {
                string xisoPath = isoFiles[i];
                try
                {
                    // Integrity audit: a missing optimized tag is reported through
                    // IsOptimized but does not fail the audit, so raw (unconverted)
                    // images pass. Use --is-optimized to require the tag.
                    AuditResult result = XisoReader.AuditXiso(xisoPath, requireOptimizedTag: false);
                    Logger.Log($"Auditing {xisoPath}:\n\n");
                    Logger.Log($"  Files checked:  {result.FilesChecked}\n");
                    Logger.Log($"  Dirs checked:   {result.DirsChecked}\n");
                    Logger.Log($"  Optimized:      {(result.IsOptimized ? "yes" : "no")}\n");

                    if (result.Issues.Count == 0)
                    {
                        Logger.Log("  Result:         PASS\n");
                    }
                    else
                    {
                        allValid = false;
                        Logger.Log($"  Result:         FAIL ({result.Issues.Count} issue(s))\n");
                        foreach (string issue in result.Issues)
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
            // CLI-012: exactly <src> <out> (-o would be silently dropped).
            // CLI-017: -d/-o/-D are ignored here.
            if (RejectIgnoredOutputFlags("validate"))
                return 1;

            if (optind + 1 >= args.Length)
            {
                PrintUsage();
                return 1;
            }

            if (optind + 2 < args.Length)
            {
                Logger.LogErr("Error: validate takes exactly <src> <out>\n");
                return 1;
            }

            string sourcePath = args[optind];
            string outputPath = args[optind + 1];

            try
            {
                ValidationResult result = XisoValidator.ValidateConversion(sourcePath, outputPath, validateChecksums);
                XisoValidator.LogResult(result, sourcePath, outputPath, validateChecksums);

                if (validateReport != null)
                {
                    XisoValidator.WriteReport(result, sourcePath, outputPath, validateReport);
                    Logger.Log($"[VALIDATE] Report written to {validateReport}\n");
                }

                if (jsonMode)
                {
                    JsonValidation payload = new(
                        1,
                        "validate",
                        sourcePath,
                        outputPath,
                        validateChecksums,
                        result.Passed,
                        new JsonValidationSide(sourcePath, result.SourceFileCount, result.SourceDirCount,
                            result.SourceTotalBytes),
                        new JsonValidationSide(outputPath, result.OutputFileCount, result.OutputDirCount,
                            result.OutputTotalBytes),
                        result.Issues.Select(static i => new JsonValidationIssue(
                            i.Type.ToString(),
                            i.Path,
                            i.SourceSize,
                            i.OutputSize,
                            i.SourceHash == null ? null : Convert.ToHexString(i.SourceHash).ToLowerInvariant(),
                            i.OutputHash == null ? null : Convert.ToHexString(i.OutputHash).ToLowerInvariant())).ToList());
                    WriteJson(payload);
                }

                return result.Passed ? 0 : 2;
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException or XisoFormatException)
            {
                Logger.LogErr($"Error validating: {ex.Message}\n");
                return 1;
            }
        }

        // CLI-017: the final loop consumes -d only for extract and -o/-D only
        // for rewrite (checked above). Pure extract ignores -o/-D; tree/list
        // ignore all three. Placed after every early-return dispatch so the
        // redump/create/info modes that own these flags are unaffected.
        if (extract && !rewrite)
        {
            if (outputName != null)
            {
                Logger.LogErr(
                    "Error: -o <output> is only used with -r (rewrite), redump modes, and the rebuild/compress/decompress verbs\n");
                return 1;
            }

            if (deleteOld)
            {
                Logger.LogErr("Error: -D is only used with -r (rewrite)\n");
                return 1;
            }
        }
        else if (!rewrite)
        {
            if (RejectIgnoredOutputFlags(tree ? "-t (tree)" : "-l (list)"))
                return 1;
        }

        List<JsonImageListing>? jsonListings = jsonMode && !rewrite && !extract ? [] : null;

        for (int i = 0; i < isoFiles.Count; i++)
        {
            isos++;
            Logger.Log("\n");
            Logger.TotalBytes = Logger.TotalFiles = 0;

            string xisoPath = isoFiles[i];

            if (IsZarPath(xisoPath))
            {
                try
                {
                    if (rewrite)
                    {
                        Logger.LogErr($"Error: cannot rewrite ZArchive '{xisoPath}'; convert it to ISO first\n");
                        err = 1;
                    }
                    else if (extract)
                    {
                        string destination = path ?? DeriveZarExtractDirectory(xisoPath);
                        Logger.Log($"extracting {Path.GetFileName(xisoPath)}:\n\n");
                        XisoZarchive.ExtractZar(xisoPath, destination);
                    }
                    else if (jsonListings != null)
                    {
                        List<JsonEntry> entries = [];
                        int fileCount = 0;
                        long totalBytes = 0;
                        foreach (ZarEntry entry in XisoZarchive.ListZar(xisoPath))
                        {
                            entries.Add(new JsonEntry(
                                entry.Path,
                                Path.GetFileName(entry.Path),
                                entry.IsDirectory,
                                0,
                                entry.Size > long.MaxValue ? long.MaxValue : (long)entry.Size,
                                0));
                            if (!entry.IsDirectory)
                            {
                                fileCount++;
                                if (entry.Size <= long.MaxValue) totalBytes += (long)entry.Size;
                            }
                        }

                        jsonListings.Add(new JsonImageListing(xisoPath, fileCount, totalBytes, entries));
                    }
                    else
                    {
                        PrintZarEntries(xisoPath, tree);
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
                {
                    Logger.LogErr($"Error processing ZArchive {xisoPath}: {ex.Message}\n");
                    err = 1;
                }

                continue;
            }

            bool optimized;

            try
            {
                // Probed through the decompressed view so .cso input is detected too.
                // skipSectors shifts the tag offset for prepended (Redump/XGD) images,
                // matching the extraction/list walks that consume `optimized`.
                optimized = XisoReader.IsOptimizedImage(xisoPath, skipSectors);
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                // CLI-011: report the actual failure instead of always
                // claiming a missing file.
                Logger.LogErr($"open error: {xisoPath}: No such file or directory\n");
                err = 1;
                continue;
            }
            catch (UnauthorizedAccessException ex)
            {
                Logger.LogErr($"open error: {xisoPath}: permission denied ({ex.Message})\n");
                err = 1;
                continue;
            }
            catch (Exception ex) when (ex is XisoFormatException or InvalidDataException)
            {
                Logger.LogErr($"open error: {xisoPath} is not a valid xbox iso image ({ex.Message})\n");
                err = 1;
                continue;
            }
            catch (Exception ex)
            {
                Logger.LogErr($"open error: {xisoPath} ({ex.Message})\n");
                err = 1;
                continue;
            }

            if (rewrite)
            {
                // #15 (xdvdfs #36): refuse an -o that points at the input or its
                // backup before anything else (even an "already optimized" skip).
                string? rewriteRefusal = CliOutputGuard.CheckRewriteOutput(xisoPath, outputName);
                if (rewriteRefusal != null)
                {
                    Logger.LogErr(rewriteRefusal);
                    err = 1;
                    continue;
                }

                if (optimized)
                {
                    Logger.Log($"{xisoPath} is already optimized, skipping...\n");
                    continue;
                }

                string oldPath = xisoPath + ".old";
                if (File.Exists(oldPath))
                {
                    Logger.LogErr($"{oldPath} already exists, cannot rewrite {xisoPath}\n");
                    continue;
                }

                // An explicit -o target may already exist (default output is the
                // just-moved-away input path, which is always free). Confirm first:
                // after the move below there is no going back.
                if (outputName != null && !OverwritePrompt.ConfirmOverwrite(outputName, assumeYes, assumeNo))
                {
                    err = 1;
                    continue;
                }

                try
                {
                    File.Move(xisoPath, oldPath);
                    int rewriteResult = XisoReader.DecodeXiso(oldPath, path, ExtractMode.Rewrite,
                        out string? newIsoPath, true, outputName: outputName, skipSectors: skipSectors,
                        prependSectors: prependSectors, preserveAttributes: preserveAttrs);

                    // The input now lives at <input>.old; a failed rewrite must
                    // not print success or exit 0. Track this file's own result
                    // so an earlier file's failure does not suppress the success
                    // output or keep the backup of a later, successful rewrite.
                    int fileErr = rewriteResult;

                    if (fileErr == 0)
                    {
                        Logger.Log($"\n{Logger.TotalFiles} files in {newIsoPath} total {Logger.TotalBytes} bytes\n");
                        Logger.Log(
                            $"\n{xisoPath} successfully rewritten{(path != null ? " as " : ".")}{(path != null ? newIsoPath : "")}\n");
                    }

                    if (fileErr == 0 && validateFlag && newIsoPath != null)
                    {
                        Logger.Log("\n");
                        ValidationResult valResult =
                            XisoValidator.ValidateConversion(oldPath, newIsoPath, validateChecksums);
                        XisoValidator.LogResult(valResult, oldPath, newIsoPath, validateChecksums);

                        if (validateReport != null)
                        {
                            XisoValidator.WriteReport(valResult, oldPath, newIsoPath, validateReport);
                            Logger.Log($"[VALIDATE] Report written to {validateReport}\n");
                        }

                        // CLI-020: a failed validation is exit 2, matching the
                        // standalone validate verb (non-strict used to log and
                        // return 0). --validate-strict is retained for CLI parity.
                        if (!valResult.Passed)
                        {
                            fileErr = 2;
                        }
                    }

                    // Only unlink the backup after a fully successful rewrite
                    // (including validation): a failed rewrite must keep the
                    // only remaining copy.
                    if (deleteOld && fileErr == 0) File.Delete(oldPath);

                    if (fileErr != 0)
                        err = fileErr;
                }
                catch (Exception ex)
                {
                    Logger.LogErr($"{ex.Message}\n");
                    err = 1;
                }
            }
            else
            {
                if (jsonListings != null)
                {
                    List<JsonEntry> entries = [];
                    int fileCount = 0;
                    long totalBytes = 0;
                    using (Stream stream = XisoReader.OpenImageStream(xisoPath))
                    {
                        CollectJsonEntries(stream, xisoPath, "/", skipSectors, entries,
                            new HashSet<(uint Sector, uint Size)>(), 0, ref fileCount, ref totalBytes);
                    }

                    jsonListings.Add(new JsonImageListing(xisoPath, fileCount, totalBytes, entries));
                    continue;
                }

                try
                {
                    if (extract)
                    {
                        XisoReader.Extract(xisoPath, path, !optimized, skipSectors: skipSectors,
                            options: unpackOptions);
                    }
                    else if (tree)
                    {
                        XisoReader.Tree(xisoPath, !optimized, skipSectors: skipSectors);
                    }
                    else
                    {
                        XisoReader.List(xisoPath, !optimized, skipSectors: skipSectors);
                    }
                }
                catch (ExtractErrorException ex) when (ex.ErrorCode == ExtractError.ErrIsoNoFiles)
                {
                    // CLI-009: an empty ISO is not a failure — but it must not
                    // clear a previous file's failure either. Leave err untouched.
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

        if (jsonListings != null)
        {
            WriteJson(new JsonListing(1, tree ? "tree" : "list", jsonListings));
            return err;
        }

        if (err == 0 && isos > 1)
            Logger.Log($"\n{Logger.TotalFilesAllIsos} files in {isos} xiso's total {Logger.TotalBytesAllIsos} bytes\n");

        if (Logger.Warned)
            Logger.Log("\nWARNING:  Warning(s) were issued during execution--review stderr!\n");

        return err;

        // CLI-001/CLI-002: the classic operational modes are mutually exclusive.
        // (-x/extract is the default, not a mode; -r/-c/rewrite/create, --unpack,
        // --checksum/--filetime/--set-filetime and the redump modes keep their own
        // guards below.)
        bool ClassicModeSelected()
        {
            return listMode || tree || info || lsMode || xexInfoMode || xbeInfoMode || repairMode || salvageMode ||
                   hashMode || copyOut || copyIn || auditMode || validateMode || sectorLayoutMode || rangesMode ||
                   isOptimizedMode;
        }

        // CLI-017: -d (path), -o (outputName) and -D (deleteOld) are consumed by
        // a few modes only (extract/rewrite/redump/rebuild/compress/decompress).
        // Every other dispatch must reject them instead of silently ignoring them.
        bool RejectIgnoredOutputFlags(string modeName)
        {
            if (path != null)
            {
                Logger.LogErr($"Error: -d <dir> is not used with {modeName}\n");
                return true;
            }

            if (outputName != null)
            {
                Logger.LogErr($"Error: -o <output> is not used with {modeName}\n");
                return true;
            }

            if (deleteOld)
            {
                Logger.LogErr("Error: -D is only used with -r (rewrite)\n");
                return true;
            }

            if (preserveAttrs)
            {
                Logger.LogErr("Error: --preserve-attrs is only used with -r (rewrite)\n");
                return true;
            }

            return false;
        }
    }

    /// <summary>
    /// Serializes one <c>--json</c> payload with the source-generated context.
    /// Writes to <see cref="Logger.Out"/> so redirected hosts and tests observe
    /// the same stream as a real stdout.
    /// </summary>
    private static void WriteJson<T>(T payload) where T : class
    {
        string json = JsonSerializer.Serialize(payload, typeof(T), JsonOutputContext.Default);
        Logger.Out.WriteLine(json);
        Logger.Out.Flush();
    }

    /// <summary>Reads the MinVer informational version (build metadata trimmed).</summary>
    private static string GetVersion(Type type)
    {
        string? info = type.Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion;
        if (!string.IsNullOrWhiteSpace(info))
        {
            int plus = info.IndexOf('+', StringComparison.Ordinal);
            return (plus >= 0 ? info[..plus] : info).Trim();
        }

        Version? version = type.Assembly.GetName().Version;
        return version?.ToString(3) ?? "unknown";
    }

    /// <summary>Reads the commit suffix MinVer appends to the informational version.</summary>
    private static string? GetCommit(Type type)
    {
        string? info = type.Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion;
        if (string.IsNullOrWhiteSpace(info)) return null;
        int plus = info.IndexOf('+', StringComparison.Ordinal);
        return plus >= 0 && plus + 1 < info.Length ? info[(plus + 1)..].Trim() : null;
    }

    /// <summary>
    /// Prints the enriched <c>-v</c>/<c>--version</c> banner: the historical
    /// one-line banner first (scripts parse that line), then component, runtime
    /// and commit details (PlanToEnhance Phase 6, extract-xiso #21).
    /// </summary>
    private static void WriteVersionInfo()
    {
        Console.Write(Constants.Banner);
        Console.Write($"  XISOSharp:      {GetVersion(typeof(Constants))}\n");
        Console.Write($"  CCISharp:       {GetVersion(typeof(CCISharp.CciFile))}\n");
        Console.Write($"  ZArchiveSharp:  {GetVersion(typeof(ZArchiveSharp.ZArchiveReader))}\n");
        Console.Write($"  Runtime:        {RuntimeInformation.FrameworkDescription}\n");
        Console.Write($"  OS:             {RuntimeInformation.OSDescription}\n");
        Console.Write($"  Architecture:   {RuntimeInformation.ProcessArchitecture}\n");
        string? commit = GetCommit(typeof(Constants));
        if (commit != null) Console.Write($"  Commit:         {commit}\n");
    }

    /// <summary>Emits the <c>--json</c> form of the version report.</summary>
    private static void WriteJsonVersion()
    {
        JsonVersion payload = new(
            1,
            "version",
            GetVersion(typeof(Constants)),
            GetVersion(typeof(CCISharp.CciFile)),
            GetVersion(typeof(ZArchiveSharp.ZArchiveReader)),
            RuntimeInformation.FrameworkDescription,
            RuntimeInformation.OSDescription,
            RuntimeInformation.ProcessArchitecture.ToString(),
            GetCommit(typeof(Constants)));
        WriteJson(payload);
    }

    /// <summary>Maps a <see cref="VolumeInfo"/> to the JSON volume shape.</summary>
    private static JsonVolume ToJsonVolume(VolumeInfo volume, string? fileTimeIso, ulong fileTimeRaw) =>
        new(
            volume.IsValid,
            volume.DiscFormat,
            volume.FileLength,
            volume.TotalSectors,
            volume.DiscLseek,
            volume.DescriptorSector,
            volume.RootDirSector,
            volume.RootDirSize,
            fileTimeIso,
            fileTimeRaw);

    /// <summary>
    /// Recursively collects every entry under <paramref name="dirPath"/> for
    /// <c>--json -l</c>/<c>-t</c>. Uses the same directory reader as the text
    /// walk (all image containers and skip offsets) and fails on a table cycle
    /// instead of recursing forever.
    /// </summary>
    private static void CollectJsonEntries(
        Stream imageStream,
        string imageName,
        string dirPath,
        int? skipSectors,
        List<JsonEntry> entries,
        HashSet<(uint Sector, uint Size)> visitedDirectories,
        int depth,
        ref int fileCount,
        ref long totalBytes)
    {
        if (depth > Constants.MaxTocDepth)
        {
            throw new XisoFormatException(
                $"invalid TOC entry at '{dirPath}': maximum directory depth {Constants.MaxTocDepth} exceeded (possible directory cycle).");
        }

        foreach (EntryInfo entry in XisoReader.ListDirectory(imageStream, imageName, dirPath, skipSectors))
        {
            string path = string.Equals(dirPath, "/", StringComparison.Ordinal)
                ? "/" + entry.Name
                : dirPath.TrimEnd('/') + "/" + entry.Name;

            entries.Add(new JsonEntry(
                path,
                entry.Name,
                entry.IsDirectory,
                entry.StartSector,
                entry.FileSize,
                entry.Attributes));

            if (entry.IsDirectory)
            {
                if (entry.FileSize > 0 && !visitedDirectories.Add((entry.StartSector, entry.FileSize)))
                {
                    throw new XisoFormatException(
                        $"invalid TOC entry at '{path}': directory cycle detected at sector {entry.StartSector}.");
                }

                CollectJsonEntries(imageStream, imageName, path, skipSectors, entries, visitedDirectories,
                    depth + 1, ref fileCount, ref totalBytes);
            }
            else
            {
                fileCount++;
                totalBytes += entry.FileSize;
            }
        }
    }

    /// <summary>Executes the <c>--unpack</c> mode: unpack one ISO to a destination directory
    /// (defaulting to the ISO name). Returns the process exit code.
    /// </summary>
    private static int RunUnpackMode(string[] args, int optind, string? path, int? skipSectors, bool skipExisting,
        bool continueOnError)
    {
        if (optind >= args.Length)
        {
            PrintUsage();
            return 1;
        }

        // CLI-012: at most <iso> [dest].
        if (optind + 2 < args.Length)
        {
            Logger.LogErr("Error: --unpack takes at most <iso> [dest] (extra arguments not allowed)\n");
            return 1;
        }

        if (path != null)
        {
            Logger.LogErr("Error: --unpack takes the destination as an argument; -d is not used with --unpack\n");
            return 1;
        }

        string xisoPath = args[optind];
        string? destPath = optind + 1 < args.Length ? args[optind + 1] : null;

        try
        {
            if (IsZarPath(xisoPath))
            {
                XisoZarchive.ExtractZar(xisoPath, destPath ?? DeriveZarExtractDirectory(xisoPath),
                    skipExisting);
                return 0;
            }

            UnpackOptions? unpackOptions = skipExisting || continueOnError
                ? new UnpackOptions { SkipExisting = skipExisting, ContinueOnError = continueOnError }
                : null;
            int result = XisoReader.UnpackImage(xisoPath, destPath, skipSectors: skipSectors,
                options: unpackOptions);
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
        bool assumeYes = false;
        bool assumeNo = false;
        List<string> positionals = new();
        for (int i = optind; i < args.Length; i++)
        {
            string a = args[i];
            if (string.Equals(a, "-o", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(a, "--output", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 >= args.Length)
                {
                    Logger.LogErr("Error: -o requires a filename\n");
                    return 1;
                }

                outRebuild = args[++i];
            }
            else if (string.Equals(a, "--security-sectors", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(a, "--sectors", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 >= args.Length)
                {
                    PrintUsage();
                    return 1;
                }

                secPath = args[++i];
            }
            else if (string.Equals(a, "-q", StringComparison.Ordinal))
            {
                Logger.Quiet = true;
            }
            else if (string.Equals(a, "-Q", StringComparison.OrdinalIgnoreCase))
            {
                Logger.Quiet = Logger.RealQuiet = true;
            }
            else if (string.Equals(a, "-y", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(a, "--yes", StringComparison.OrdinalIgnoreCase))
            {
                assumeYes = true;
            }
            else if (string.Equals(a, "-n", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(a, "--no", StringComparison.OrdinalIgnoreCase))
            {
                assumeNo = true;
            }
            else if (string.Equals(a, "-h", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(a, "--help", StringComparison.OrdinalIgnoreCase))
            {
                PrintUsage();
                return 0;
            }
            else if (string.Equals(a, "-v", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(a, "--version", StringComparison.OrdinalIgnoreCase))
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
            Logger.LogErr("Error: rebuild requires at least <xiso|game.zar> [video.iso] [filler|seed] [su...]\n");
            PrintUsage();
            return 1;
        }

        if (assumeYes && assumeNo)
        {
            Logger.LogErr("[ERROR] Cannot use both --no (-n) and --yes (-y)\n");
            return 1;
        }

        string xisoPath = positionals[0];
        string? videoPath = positionals.Count > 1 ? positionals[1] : null;
        string? fillerOrSeed = positionals.Count > 2 ? positionals[2] : null;
        string? updatePath = positionals.Count > 3 ? positionals[3] : null;

        if (positionals.Count > 4)
        {
            Logger.LogErr(
                "Error: rebuild takes at most 4 positional files: <xiso|game.zar> [video.iso] [filler|seed] [update]\n");
            return 1;
        }

        // If video path not provided, try to derive from xiso directory.
        // CLI-006: keep null when the derived candidate does not exist so the
        // rebuild fails with a clear message instead of a phantom path.
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
        }

        if (videoPath == null)
        {
            Logger.LogErr(
                $"Error: no video partition found beside {xisoPath} (looked for <name>.video.iso); pass [video.iso] explicitly\n");
            return 1;
        }

        string outRedump = outRebuild ?? DeriveRedumpPath(xisoPath);

        // #15 (xdvdfs #36): the output is written while the parts are read.
        string? rebuildRefusal = CliOutputGuard.CheckRebuildOutput(outRedump, secPath,
            xisoPath, videoPath, fillerOrSeed, updatePath);
        if (rebuildRefusal != null)
        {
            Logger.LogErr(rebuildRefusal);
            return 1;
        }

        if (!OverwritePrompt.ConfirmOverwrite(outRedump, assumeYes, assumeNo))
            return 1;

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
        List<string> mapRaw = new();
        string? metaOutput = null;
        bool dryRun = false;
        ulong? fileTime = null;
        string? layoutFile = null;
        List<string> positionals = new();

        for (int i = optind; i < args.Length; i++)
        {
            string a = args[i];
            if (string.Equals(a, "-f", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(a, "--file", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 >= args.Length)
                {
                    Logger.LogErr("Error: -f requires a filename\n");
                    return 1;
                }

                specFile = args[++i];
            }
            else if (string.Equals(a, "-m", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(a, "--map", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 >= args.Length)
                {
                    Logger.LogErr("Error: -m requires a rule\n");
                    return 1;
                }

                mapRaw.Add(args[++i]);
            }
            else if (string.Equals(a, "-O", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(a, "--output", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 >= args.Length)
                {
                    Logger.LogErr("Error: -O requires a path\n");
                    return 1;
                }

                metaOutput = args[++i];
            }
            else if (string.Equals(a, "-D", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(a, "--dry-run", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(a, "--dryrun", StringComparison.OrdinalIgnoreCase))
            {
                dryRun = true;
            }
            else if (string.Equals(a, "--file-time", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 >= args.Length ||
                    !FileTimeHelper.TryParseFileTime(args[i + 1], out ulong raw, out _))
                {
                    Logger.LogErr(
                        "Error: --file-time requires a value (ISO-8601, decimal raw, 0x hex, 'now', or '0' for deterministic images)\n");
                    return 1;
                }

                fileTime = raw;
                i++;
            }
            else if (string.Equals(a, "--layout", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 >= args.Length || args[i + 1].StartsWith('-'))
                {
                    Logger.LogErr("Error: --layout requires a layout file\n");
                    return 1;
                }

                layoutFile = args[++i];
            }
            else if (string.Equals(a, "-h", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(a, "--help", StringComparison.OrdinalIgnoreCase))
            {
                PrintUsage();
                return 0;
            }
            else if (string.Equals(a, "-v", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(a, "--version", StringComparison.OrdinalIgnoreCase))
            {
                Console.Write(Constants.Banner);
                return 0;
            }
            else if (string.Equals(a, "-q", StringComparison.Ordinal))
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
                        (File.GetAttributes(sourcePathStr) & FileAttributes.Directory) != FileAttributes.None;
            }
            catch
            {
                // ignored
            }

            try
            {
                isFile = File.Exists(sourcePathStr);
            }
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

        List<RemapRule> rules = new();
        string? specOutput = null;

        if (mapRaw.Count > 0)
        {
            foreach (string raw in mapRaw)
            {
                if (!RemapRule.TryParse(raw, out RemapRule? r, out string? err))
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
            string trimmed = sourceDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string parent = Path.GetDirectoryName(trimmed) ?? Directory.GetCurrentDirectory();
            string name = Path.GetFileName(trimmed);
            if (string.IsNullOrEmpty(name)) name = "image";
            // with_extension is_dir=true logic: append .xiso.iso
            outputIso = Path.Combine(parent, name + ".xiso.iso");
            outputIso = Path.GetFullPath(outputIso);
        }

        if (dryRun)
        {
            try
            {
                IReadOnlyList<(string HostPath, string ImagePath)> list = RemapFilesystem.DryRunRemap(sourceDir, rules);
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
            string? outDir = Path.GetDirectoryName(outputIso);
            if (!string.IsNullOrEmpty(outDir))
                Directory.CreateDirectory(outDir);

            XisoLayoutSpec? layout = null;
            if (layoutFile != null)
            {
                try
                {
                    layout = XisoLayoutSpec.Load(layoutFile);
                }
                catch (Exception ex) when (ex is IOException or FormatException or UnauthorizedAccessException or ArgumentException)
                {
                    Logger.LogErr($"Error: cannot read sector layout {layoutFile}: {ex.Message}\n");
                    return 1;
                }
            }

            Logger.Log($"{Constants.Banner}");
            return RemapFilesystem.BuildImage(sourceDir, outputIso, rules, fileTime: fileTime, layout: layout);
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
        List<string> mapRaw = new();
        string? metaOutput = null;
        List<string> positionals = new();
        for (int i = optind; i < args.Length; i++)
        {
            string a = args[i];
            if (string.Equals(a, "-m", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(a, "--map", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 >= args.Length)
                {
                    Logger.LogErr("Error: -m requires a rule\n");
                    return 1;
                }

                mapRaw.Add(args[++i]);
            }
            else if (string.Equals(a, "-O", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(a, "--output", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 >= args.Length)
                {
                    Logger.LogErr("Error: -O requires a path\n");
                    return 1;
                }

                metaOutput = args[++i];
            }
            else if (string.Equals(a, "-h", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(a, "--help", StringComparison.OrdinalIgnoreCase))
            {
                PrintUsage();
                return 0;
            }
            else if (string.Equals(a, "-v", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(a, "--version", StringComparison.OrdinalIgnoreCase))
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

        List<RemapRule> rules = new();
        foreach (string raw in mapRaw)
        {
            if (!RemapRule.TryParse(raw, out RemapRule? r, out string? err))
            {
                Logger.LogErr($"Error: invalid map rule \"{raw}\": {err}\n");
                return 1;
            }

            rules.Add(r!);
        }

        string? outFile = positionals.Count == 1 ? positionals[0] : null;
        string toml = RemapFilesystem.GenerateSpecText(rules, metaOutput);
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

    private static int RunCompressMode(string[] args, int optind, string defaultFormat = "cso")
    {
        string? output = null;
        int level = 9;
        string format = defaultFormat;
        // xdvdfs compress always writes through ciso::split::SplitOutput (split point 0xffbf6000,
        // ~4 GiB). Match that by default; --ciso-split overrides the point, --ciso-split 0
        // writes a single .cso. CCI uses the same ~4 GiB default.
        long? cisoSplitBytes = CisoWriter.DefaultSplitPoint;
        long? cciSplitBytes = XisoCci.DefaultSplitBytes;
        byte version = CisoWriter.VersionLz4;
        bool assumeYes = false;
        bool assumeNo = false;
        bool cisoLevelSpecified = false;
        bool cciLevelSpecified = false;
        bool cisoVersionSpecified = false;
        bool cisoSplitSpecified = false;
        bool cciSplitSpecified = false;
        BatchOptions batch = new();
        List<string> positionals = new();

        for (int i = optind; i < args.Length; i++)
        {
            string a = args[i];
            if (BatchConversion.TryConsume(args, ref i, batch, out string? batchFlagError))
            {
                if (batchFlagError != null)
                {
                    Logger.LogErr($"Error: {batchFlagError}\n");
                    return 1;
                }
            }
            else if (string.Equals(a, "-o", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(a, "--output", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 >= args.Length)
                {
                    Logger.LogErr("Error: -o requires a filename\n");
                    return 1;
                }

                output = args[++i];
            }
            else if (string.Equals(a, "--format", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 >= args.Length)
                {
                    Logger.LogErr("Error: --format requires cso or cci\n");
                    return 1;
                }

                string value = args[++i];
                if (string.Equals(value, "cso", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(value, "ciso", StringComparison.OrdinalIgnoreCase))
                {
                    format = "cso";
                }
                else if (string.Equals(value, "cci", StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(value, "ccim", StringComparison.OrdinalIgnoreCase))
                {
                    format = "cci";
                }
                else
                {
                    Logger.LogErr("Error: --format requires cso or cci\n");
                    return 1;
                }
            }
            else if (string.Equals(a, "--ciso-level", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(a, "-l", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(a, "--level", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 >= args.Length || !int.TryParse(args[i + 1], CultureInfo.InvariantCulture, out level) ||
                    level < 0 || level > 9)
                {
                    Logger.LogErr("Error: --ciso-level requires an integer 0..9\n");
                    return 1;
                }

                cisoLevelSpecified = string.Equals(a, "--ciso-level", StringComparison.OrdinalIgnoreCase);
                i++;
            }
            else if (string.Equals(a, "--cci-level", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 >= args.Length || !int.TryParse(args[i + 1], CultureInfo.InvariantCulture, out level) ||
                    level < 0 || level > 9)
                {
                    Logger.LogErr("Error: --cci-level requires an integer 0..9\n");
                    return 1;
                }

                cciLevelSpecified = true;
                i++;
            }
            else if (string.Equals(a, "--ciso-version", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 >= args.Length)
                {
                    Logger.LogErr("Error: --ciso-version requires 1, 2 or auto\n");
                    return 1;
                }

                string v = args[++i];
                if (string.Equals(v, "1", StringComparison.OrdinalIgnoreCase))
                {
                    version = CisoWriter.VersionDeflate;
                }
                else if (string.Equals(v, "2", StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(v, "auto", StringComparison.OrdinalIgnoreCase))
                {
                    version = CisoWriter.VersionLz4;
                }
                else
                {
                    Logger.LogErr("Error: --ciso-version requires 1, 2 or auto\n");
                    return 1;
                }

                cisoVersionSpecified = true;
            }
            else if (string.Equals(a, "--ciso-split", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 >= args.Length || !long.TryParse(args[i + 1], CultureInfo.InvariantCulture, out long sb) ||
                    sb < 0)
                {
                    Logger.LogErr("Error: --ciso-split requires a non-negative integer (bytes)\n");
                    return 1;
                }

                cisoSplitBytes = sb == 0 ? null : sb;
                cisoSplitSpecified = true;
                i++;
            }
            else if (string.Equals(a, "--cci-split", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 >= args.Length || !long.TryParse(args[i + 1], CultureInfo.InvariantCulture, out long sb) ||
                    sb < 0)
                {
                    Logger.LogErr("Error: --cci-split requires a non-negative integer (bytes)\n");
                    return 1;
                }

                cciSplitBytes = sb == 0 ? null : sb;
                cciSplitSpecified = true;
                i++;
            }
            else if (string.Equals(a, "--split", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 >= args.Length || !long.TryParse(args[i + 1], CultureInfo.InvariantCulture, out long sb) ||
                    sb < 0)
                {
                    Logger.LogErr("Error: --split requires a non-negative integer (bytes)\n");
                    return 1;
                }

                cisoSplitBytes = sb == 0 ? null : sb;
                cciSplitBytes = sb == 0 ? null : sb;
                i++;
            }
            else if (string.Equals(a, "-y", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(a, "--yes", StringComparison.OrdinalIgnoreCase))
            {
                assumeYes = true;
            }
            else if (string.Equals(a, "-n", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(a, "--no", StringComparison.OrdinalIgnoreCase))
            {
                assumeNo = true;
            }
            else if (string.Equals(a, "-q", StringComparison.Ordinal))
            {
                Logger.Quiet = true;
            }
            else if (string.Equals(a, "-Q", StringComparison.OrdinalIgnoreCase))
            {
                Logger.Quiet = Logger.RealQuiet = true;
            }
            else if (string.Equals(a, "-h", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(a, "--help", StringComparison.OrdinalIgnoreCase))
            {
                PrintUsage();
                return 0;
            }
            else if (string.Equals(a, "-v", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(a, "--version", StringComparison.OrdinalIgnoreCase))
            {
                Console.Write(Constants.Banner);
                return 0;
            }
            else if (a.StartsWith('-'))
            {
                Logger.LogErr($"Error: unknown option for compress: {a}\n");
                PrintUsage();
                return 1;
            }
            else
            {
                positionals.Add(a);
            }
        }

        if (assumeYes && assumeNo)
        {
            Logger.LogErr("[ERROR] Cannot use both --no (-n) and --yes (-y)\n");
            return 1;
        }

        bool cci = string.Equals(format, "cci", StringComparison.OrdinalIgnoreCase);
        if (cci && cisoLevelSpecified)
        {
            Logger.LogErr("Error: --ciso-level is only valid for CISO output; use --cci-level\n");
            return 1;
        }

        if (!cci && cciLevelSpecified)
        {
            Logger.LogErr("Error: --cci-level is only valid for CCI output; use --ciso-level\n");
            return 1;
        }

        if (cci && cisoVersionSpecified)
        {
            Logger.LogErr("Error: --ciso-version is only valid for CISO output\n");
            return 1;
        }

        if (cci && cisoSplitSpecified)
        {
            Logger.LogErr("Error: --ciso-split is only valid for CISO output; use --cci-split\n");
            return 1;
        }

        if (!cci && cciSplitSpecified)
        {
            Logger.LogErr("Error: --cci-split is only valid for CCI output; use --ciso-split\n");
            return 1;
        }

        string? batchError = BatchConversion.Validate(batch);
        if (batchError != null)
        {
            Logger.LogErr($"Error: {batchError}\n");
            return 1;
        }

        if (batch.Directory != null)
        {
            if (positionals.Count > 0 || output != null)
            {
                Logger.LogErr("Error: --batch cannot be combined with an explicit source or output\n");
                return 1;
            }

            batch.AssumeYes = assumeYes;
            batch.AssumeNo = assumeNo;
            return RunCompressBatch(batch, cci, level, cisoSplitBytes, cciSplitBytes, version);
        }

        if (positionals.Count < 1 || positionals.Count > 2)
        {
            Logger.LogErr("Error: compress requires <source> [output.cso|output.cci]\n");
            PrintUsage();
            return 1;
        }

        // CLI-005: a positional output and -o together are ambiguous; the
        // positional used to win and -o was silently dropped.
        if (positionals.Count == 2 && output != null)
        {
            Logger.LogErr("Error: compress takes either [output] or -o <output>, not both\n");
            return 1;
        }

        string source = positionals[0];
        string? outPath = positionals.Count == 2 ? positionals[1] : output;
        long? effectiveSplit = cci ? cciSplitBytes : cisoSplitBytes;

        // #15 (xdvdfs #36): refuse before the -y/-n prompt, not after it.
        string probeBase = outPath ?? (cci
            ? XisoCci.DeriveDefaultCciPath(source, Directory.Exists(source))
            : CisoWriter.DeriveDefaultCsoPath(source, Directory.Exists(source)));
        string? compressRefusal = CliOutputGuard.CheckImageOutput(source, probeBase);
        if (compressRefusal != null)
        {
            Logger.LogErr(compressRefusal);
            return 1;
        }

        // Resolve the path the writer will create (first split part when
        // splitting) so an existing output triggers the -y/-n prompt instead
        // of a silent overwrite.
        string probePath = probeBase;
        if (effectiveSplit.HasValue)
        {
            probePath = cci
                ? Path.ChangeExtension(probeBase, "1.cci")
                : Path.ChangeExtension(probeBase, "1.cso");
        }
        else if (cci && !probePath.EndsWith(".cci", StringComparison.OrdinalIgnoreCase))
        {
            probePath += ".cci";
        }

        // Item #36: the base-name check cannot see split parts; a source named
        // like its own first part used to pass and fail later inside the writer.
        if (effectiveSplit.HasValue)
        {
            if (XisoPaths.AreSamePath(source, probePath))
            {
                Logger.LogErr($"Error: output part {probePath} is the same file as the input {source};" +
                              " choose another name\n");
                return 1;
            }
        }

        if (!OverwritePrompt.ConfirmOverwrite(probePath, assumeYes, assumeNo))
            return 1;

        try
        {
            Logger.Log(Constants.Banner);
            if (cci)
            {
                return XisoCci.CompressToCci(source, outPath, level, cciSplitBytes ?? 0, overwrite: true);
            }

            return CisoWriter.CompressToCso(source, outPath, level, cisoSplitBytes, version);
        }
        catch (Exception ex)
        {
            Logger.LogErr($"Error compressing: {ex.Message}\n");
            return 1;
        }
    }

    /// <summary>
    /// Phase 5: <c>compress --batch</c>. Converts every plain ISO in the batch
    /// directory to CISO or CCI, with the shared skip/continue/parallel and
    /// naming-template policy from <see cref="BatchConversion"/>.
    /// </summary>
    private static int RunCompressBatch(
        BatchOptions batch,
        bool cci,
        int level,
        long? cisoSplitBytes,
        long? cciSplitBytes,
        byte version)
    {
        string format = cci ? "cci" : "cso";
        string partExtension = cci ? "1.cci" : "1.cso";
        bool split = cci ? cciSplitBytes.HasValue : cisoSplitBytes.HasValue;

        List<string> sources;
        try
        {
            sources = BatchConversion.EnumerateSources(batch.Directory!, batch.Recursive,
                includeFirstPart: true, ".iso", ".xiso");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Logger.LogErr($"Error: {ex.Message}\n");
            return 1;
        }

        if (sources.Count == 0)
        {
            Logger.LogErr($"Error: no .iso/.xiso files found in {batch.Directory}\n");
            return 1;
        }

        Logger.Log(Constants.Banner);
        return BatchConversion.Run(
            "compress",
            batch,
            sources,
            source => BatchConversion.OutputPath(source, batch.OutputName, format, appendExtension: true),
            (source, output) => split ? [Path.ChangeExtension(output, partExtension)] : [output],
            (source, output) => cci
                ? XisoCci.CompressToCci(source, output, level, cciSplitBytes ?? 0, overwrite: true)
                : CisoWriter.CompressToCso(source, output, level, cisoSplitBytes, version),
            (source, output) =>
            {
                string? refusal = CliOutputGuard.CheckImageOutput(source, output);
                if (refusal != null)
                    return refusal;

                return split
                    ? CliOutputGuard.CheckImageOutput(source, Path.ChangeExtension(output, partExtension))
                    : null;
            });
    }

    private static int RunDecompressMode(string[] args, int optind)
    {
        string? output = null;
        bool assumeYes = false;
        bool assumeNo = false;
        BatchOptions batch = new();
        List<string> positionals = new();

        for (int i = optind; i < args.Length; i++)
        {
            string a = args[i];
            if (BatchConversion.TryConsume(args, ref i, batch, out string? batchFlagError))
            {
                if (batchFlagError != null)
                {
                    Logger.LogErr($"Error: {batchFlagError}\n");
                    return 1;
                }
            }
            else if (string.Equals(a, "-o", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(a, "--output", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 >= args.Length)
                {
                    Logger.LogErr("Error: -o requires a filename\n");
                    return 1;
                }

                output = args[++i];
            }
            else if (string.Equals(a, "-y", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(a, "--yes", StringComparison.OrdinalIgnoreCase))
            {
                assumeYes = true;
            }
            else if (string.Equals(a, "-n", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(a, "--no", StringComparison.OrdinalIgnoreCase))
            {
                assumeNo = true;
            }
            else if (string.Equals(a, "-q", StringComparison.Ordinal))
            {
                Logger.Quiet = true;
            }
            else if (string.Equals(a, "-Q", StringComparison.OrdinalIgnoreCase))
            {
                Logger.Quiet = Logger.RealQuiet = true;
            }
            else if (string.Equals(a, "-h", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(a, "--help", StringComparison.OrdinalIgnoreCase))
            {
                PrintUsage();
                return 0;
            }
            else if (string.Equals(a, "-v", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(a, "--version", StringComparison.OrdinalIgnoreCase))
            {
                Console.Write(Constants.Banner);
                return 0;
            }
            else if (a.StartsWith('-'))
            {
                Logger.LogErr($"Error: unknown option for decompress: {a}\n");
                PrintUsage();
                return 1;
            }
            else
            {
                positionals.Add(a);
            }
        }

        if (assumeYes && assumeNo)
        {
            Logger.LogErr("[ERROR] Cannot use both --no (-n) and --yes (-y)\n");
            return 1;
        }

        string? batchError = BatchConversion.Validate(batch);
        if (batchError != null)
        {
            Logger.LogErr($"Error: {batchError}\n");
            return 1;
        }

        if (batch.Directory != null)
        {
            if (positionals.Count > 0 || output != null)
            {
                Logger.LogErr("Error: --batch cannot be combined with an explicit source or output\n");
                return 1;
            }

            batch.AssumeYes = assumeYes;
            batch.AssumeNo = assumeNo;
            return RunDecompressBatch(batch);
        }

        if (positionals.Count < 1 || positionals.Count > 2)
        {
            Logger.LogErr("Error: decompress requires <cso|cci> [output.iso]\n");
            PrintUsage();
            return 1;
        }

        // CLI-005: a positional output and -o together are ambiguous; the
        // positional used to win and -o was silently dropped.
        if (positionals.Count == 2 && output != null)
        {
            Logger.LogErr("Error: decompress takes either [output.iso] or -o <output>, not both\n");
            return 1;
        }

        string source = positionals[0];
        string? outIso = positionals.Count == 2 ? positionals[1] : output;

        bool isCci = Path.GetExtension(source).Equals(".cci", StringComparison.OrdinalIgnoreCase);
        if (!isCci && XisoImageSource.TryDetect(source, out XisoImageSourceInfo? info) && info is not null)
        {
            isCci = info.Format is XisoImageFormat.Cci or XisoImageFormat.SplitCci;
        }

        // #15 (xdvdfs #36): refuse before the -y/-n prompt, not after it.
        string probeIso = outIso ?? (isCci
            ? XisoCci.DeriveDefaultIsoPath(source)
            : CisoReader.DeriveDefaultIsoPath(source));
        string? decompressRefusal = CliOutputGuard.CheckImageOutput(source, probeIso);
        if (decompressRefusal != null)
        {
            Logger.LogErr(decompressRefusal);
            return 1;
        }

        // Resolve the path the decompressor will write so an existing output
        // triggers the -y/-n prompt instead of a silent overwrite.
        if (!OverwritePrompt.ConfirmOverwrite(probeIso, assumeYes,
                assumeNo))
        {
            return 1;
        }

        try
        {
            Logger.Log(Constants.Banner);
            int rc = isCci
                ? XisoCci.DecompressToIso(source, outIso, overwrite: true)
                : CisoReader.DecompressToIso(source, outIso);
            return rc;
        }
        catch (Exception ex)
        {
            Logger.LogErr($"Error decompressing: {ex.Message}\n");
            return 1;
        }
    }

    /// <summary>
    /// Phase 5: <c>decompress --batch</c>. Converts every CISO/CCI (single or
    /// split first part) in the batch directory back to a plain ISO.
    /// </summary>
    private static int RunDecompressBatch(BatchOptions batch)
    {
        List<string> sources;
        try
        {
            sources = BatchConversion.EnumerateSources(batch.Directory!, batch.Recursive,
                includeFirstPart: true, ".cso", ".cci");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Logger.LogErr($"Error: {ex.Message}\n");
            return 1;
        }

        if (sources.Count == 0)
        {
            Logger.LogErr($"Error: no .cso/.cci files found in {batch.Directory}\n");
            return 1;
        }

        Logger.Log(Constants.Banner);
        return BatchConversion.Run(
            "decompress",
            batch,
            sources,
            source => BatchConversion.OutputPath(source, batch.OutputName, "iso", appendExtension: true),
            (source, output) => [output],
            (source, output) =>
            {
                bool isCci = Path.GetExtension(source).Equals(".cci", StringComparison.OrdinalIgnoreCase);
                if (!isCci && XisoImageSource.TryDetect(source, out XisoImageSourceInfo? info) && info is not null)
                {
                    isCci = info.Format is XisoImageFormat.Cci or XisoImageFormat.SplitCci;
                }

                return isCci
                    ? XisoCci.DecompressToIso(source, output, overwrite: true)
                    : CisoReader.DecompressToIso(source, output);
            },
            CliOutputGuard.CheckImageOutput);
    }

    private static bool IsZarPath(string path) =>
        Path.GetExtension(path).Equals(".zar", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Returns a refusal message when <paramref name="path"/> is a container the
    /// raw-sector redump modes (video/filler/seed/wipe/trim/petrify/update/ZAR)
    /// cannot process, or <c>null</c> for a plain ISO/XISO. Those modes read and
    /// write absolute sectors through a raw <see cref="FileStream"/>, so handing
    /// them a compressed or split container used to parse container bytes as an
    /// XISO and fail with a confusing TOC error (or produce garbage output).
    /// </summary>
    private static string? RedumpInputRefusal(string path)
    {
        if (XisoSplitter.IsSplitPath(path))
        {
            return $"'{path}' is a split-ISO part; join it first (XISOSharp join).";
        }

        if (IsZarPath(path))
        {
            return $"'{path}' is a ZArchive; convert it to ISO first (XISOSharp convert).";
        }

        if (XisoImageSource.IsCompressedContainerPath(path))
        {
            return $"'{path}' is a compressed CISO/CCI container; decompress it first (XISOSharp decompress).";
        }

        if (!XisoImageSource.TryDetect(path, out XisoImageSourceInfo? info) || info is null)
        {
            return null;
        }

        return info.Format switch
        {
            XisoImageFormat.Iso => null,
            XisoImageFormat.SplitIso => $"'{path}' is a split-ISO part; join it first (XISOSharp join).",
            XisoImageFormat.Ciso or XisoImageFormat.SplitCiso =>
                $"'{path}' is a CISO container; decompress it first (XISOSharp decompress).",
            XisoImageFormat.Cci or XisoImageFormat.SplitCci =>
                $"'{path}' is a CCI container; decompress it first (XISOSharp decompress).",
            XisoImageFormat.Zar => $"'{path}' is a ZArchive; convert it to ISO first (XISOSharp convert).",
            _ => null,
        };
    }

    private static string DeriveZarExtractDirectory(string zarPath)
    {
        string directory = Path.GetDirectoryName(zarPath) ?? string.Empty;
        string name = Path.GetFileNameWithoutExtension(zarPath);
        return Path.Combine(directory, name);
    }

    private static void PrintZarEntries(string zarPath, bool tree)
    {
        IReadOnlyList<ZarEntry> entries = XisoZarchive.ListZar(zarPath);
        string name = Path.GetFileName(zarPath);
        Logger.Log($"{(tree ? "tree" : "listing")} {name}:\n\n");

        foreach (ZarEntry entry in entries)
        {
            if (tree)
            {
                int depth = Math.Max(0, entry.Path.Count(static c => c == '/') - 1);
                string leaf = entry.Path[(entry.Path.LastIndexOf('/') + 1)..];
                Logger.Log($"{new string(' ', depth * 2)}{leaf}{(entry.IsDirectory ? "\\" : "")} ({entry.Size} bytes)\n");
            }
            else
            {
                Logger.Log($"{entry.Path.Replace('/', '\\')} ({entry.Size} bytes)\n");
            }
        }

        long files = entries.LongCount(static entry => !entry.IsDirectory);
        ulong total = 0;
        foreach (ZarEntry entry in entries)
        {
            if (!entry.IsDirectory) total += entry.Size;
        }

        Logger.Log($"\n{files} files in {name} total {total} bytes\n");
    }

    private static int RunChecksumMode(string[] args, int optind)
    {
        bool silent = false;
        bool json = false;
        List<string> positionals = new();

        for (int i = optind; i < args.Length; i++)
        {
            string a = args[i];
            if (string.Equals(a, "--silent", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(a, "-s", StringComparison.OrdinalIgnoreCase))
            {
                silent = true;
            }
            else if (string.Equals(a, "--json", StringComparison.OrdinalIgnoreCase))
            {
                json = true;
            }
            else if (string.Equals(a, "-q", StringComparison.Ordinal))
            {
                Logger.Quiet = true;
            }
            else if (string.Equals(a, "-Q", StringComparison.OrdinalIgnoreCase))
            {
                Logger.Quiet = Logger.RealQuiet = true;
            }
            else if (string.Equals(a, "-h", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(a, "--help", StringComparison.OrdinalIgnoreCase))
            {
                PrintUsage();
                return 0;
            }
            else if (string.Equals(a, "-v", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(a, "--version", StringComparison.OrdinalIgnoreCase))
            {
                if (json) WriteJsonVersion();
                else WriteVersionInfo();
                return 0;
            }
            else if (a.StartsWith('-'))
            {
                Logger.LogErr($"Error: unknown option for checksum: {a}\n");
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
            Logger.LogErr("Error: checksum requires at least one image\n");
            PrintUsage();
            return 1;
        }

        int exit = 0;
        List<JsonChecksum>? jsonChecksums = json ? [] : null;
        foreach (string iso in positionals)
        {
            try
            {
                byte[] hash = IsZarPath(iso)
                    ? XisoChecksum.ComputeZarChecksum(iso)
                    : XisoChecksum.ComputeImageChecksum(iso);
                string hex = Convert.ToHexString(hash).ToLowerInvariant();
                if (silent)
                    Logger.Log($"{hex}\n");
                else
                    Logger.Log($"{hex}\t{iso}\n");
                jsonChecksums?.Add(new JsonChecksum(iso, "sha3-256", hex));
            }
            catch (Exception ex)
            {
                Logger.LogErr($"Error checksumming {iso}: {ex.Message}\n");
                exit = 1;
            }
        }

        if (jsonChecksums != null)
        {
            WriteJson(new JsonChecksums(1, "checksum", jsonChecksums));
        }

        return exit;
    }

    /// <summary>
    /// CLI-008: the part paths a split of <paramref name="iso"/> would write
    /// (same layout math as <see cref="XisoSplitter"/>), filtered to the ones
    /// that already exist. Empty when the source cannot be probed — the split
    /// call itself then reports the real error and nothing is deleted.
    /// </summary>
    private static List<string> FindExistingSplitParts(string iso, string splitBase, long partSize, bool halves)
    {
        List<string> existing = new();
        try
        {
            if (!File.Exists(iso) || !XisoReader.GetVolumeInfo(iso).IsValid)
                return existing;
            long length = new FileInfo(iso).Length;
            if (length <= 0)
                return existing;
            long chunk;
            if (halves)
            {
                long cut = (((length + 1) / 2) + Constants.SectorSize - 1) / Constants.SectorSize *
                           Constants.SectorSize;
                chunk = cut <= 0 || cut >= length ? length : cut;
            }
            else
            {
                chunk = (partSize / Constants.SectorSize) * Constants.SectorSize;
                if (chunk <= 0)
                    return existing;
            }

            int partCount = (int)Math.Min(int.MaxValue, ((length - 1) / chunk) + 1);
            for (int i = 0; i < partCount; i++)
            {
                string part = XisoSplitter.PartPath(splitBase, i);
                if (File.Exists(part))
                    existing.Add(part);
            }
        }
        catch
        {
            // Probing must never fail the command; Split reports real errors.
            existing.Clear();
        }

        return existing;
    }

    private static int RunConvertMode(string[] args, int optind)
    {
        bool assumeYes = false;
        bool assumeNo = false;
        string? format = null;
        BatchOptions batch = new();
        List<string> positionals = new();

        for (int i = optind; i < args.Length; i++)
        {
            string a = args[i];
            if (BatchConversion.TryConsume(args, ref i, batch, out string? batchFlagError))
            {
                if (batchFlagError != null)
                {
                    Logger.LogErr($"Error: {batchFlagError}\n");
                    return 1;
                }
            }
            else if (string.Equals(a, "--format", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 >= args.Length)
                {
                    Logger.LogErr("Error: convert --format requires iso or zar\n");
                    return 1;
                }

                string value = args[++i];
                if (string.Equals(value, "iso", StringComparison.OrdinalIgnoreCase))
                {
                    format = "iso";
                }
                else if (string.Equals(value, "zar", StringComparison.OrdinalIgnoreCase))
                {
                    format = "zar";
                }
                else
                {
                    Logger.LogErr("Error: convert --format requires iso or zar\n");
                    return 1;
                }
            }
            else if (string.Equals(a, "-y", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(a, "--yes", StringComparison.OrdinalIgnoreCase))
            {
                assumeYes = true;
            }
            else if (string.Equals(a, "-n", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(a, "--no", StringComparison.OrdinalIgnoreCase))
            {
                assumeNo = true;
            }
            else if (string.Equals(a, "-q", StringComparison.Ordinal))
            {
                Logger.Quiet = true;
            }
            else if (string.Equals(a, "-Q", StringComparison.OrdinalIgnoreCase))
            {
                Logger.Quiet = Logger.RealQuiet = true;
            }
            else if (string.Equals(a, "-h", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(a, "--help", StringComparison.OrdinalIgnoreCase))
            {
                PrintUsage();
                return 0;
            }
            else if (a.StartsWith('-'))
            {
                Logger.LogErr($"Error: unknown option for convert: {a}\n");
                PrintUsage();
                return 1;
            }
            else
            {
                positionals.Add(a);
            }
        }

        if (assumeYes && assumeNo)
        {
            Logger.LogErr("[ERROR] Cannot use both --no (-n) and --yes (-y)\n");
            return 1;
        }

        string? batchError = BatchConversion.Validate(batch);
        if (batchError != null)
        {
            Logger.LogErr($"Error: {batchError}\n");
            return 1;
        }

        if (batch.Directory != null)
        {
            if (positionals.Count > 0)
            {
                Logger.LogErr("Error: --batch cannot be combined with explicit source or output paths\n");
                return 1;
            }

            batch.AssumeYes = assumeYes;
            batch.AssumeNo = assumeNo;
            return RunConvertBatch(batch, format ?? "zar");
        }

        if (format != null)
        {
            Logger.LogErr("Error: --format requires --batch; single-file convert infers the direction from the extensions\n");
            return 1;
        }

        if (positionals.Count != 2)
        {
            Logger.LogErr("Error: convert requires <source.zar|source.iso> <output.iso|output.zar>\n");
            return 1;
        }

        string source = positionals[0];
        string output = positionals[1];
        if (!OverwritePrompt.ConfirmOverwrite(output, assumeYes, assumeNo)) return 1;

        try
        {
            bool sourceZar = IsZarPath(source);
            bool outputZar = IsZarPath(output);
            if (sourceZar && !outputZar)
            {
                return XisoZarchive.ConvertZarToIso(source, output, overwrite: true) == 0 ? 0 : 1;
            }

            if (!sourceZar && outputZar)
            {
                if (!File.Exists(source))
                {
                    Logger.LogErr($"Error: source image not found: {source}\n");
                    return 1;
                }

                return XisoZarchive.CreateZar(source, output) ? 0 : 1;
            }

            Logger.LogErr("Error: convert supports .zar -> .iso and .iso -> .zar\n");
            return 1;
        }
        catch (Exception ex)
        {
            Logger.LogErr($"Error converting: {ex.Message}\n");
            return 1;
        }
    }

    /// <summary>
    /// Phase 5: <c>convert --batch --format iso|zar</c>. <c>zar</c> (the
    /// default) packs every plain ISO into a ZArchive; <c>iso</c> expands every
    /// ZArchive back to a plain ISO.
    /// </summary>
    private static int RunConvertBatch(BatchOptions batch, string format)
    {
        bool toZar = string.Equals(format, "zar", StringComparison.OrdinalIgnoreCase);

        List<string> sources;
        try
        {
            sources = BatchConversion.EnumerateSources(batch.Directory!, batch.Recursive,
                includeFirstPart: false,
                extensions: toZar ? [".iso", ".xiso"] : [".zar"]);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Logger.LogErr($"Error: {ex.Message}\n");
            return 1;
        }

        if (sources.Count == 0)
        {
            string kind = toZar ? "unsplit .iso/.xiso" : ".zar";
            Logger.LogErr($"Error: no {kind} files found in {batch.Directory}\n");
            return 1;
        }

        return BatchConversion.Run(
            "convert",
            batch,
            sources,
            source => BatchConversion.OutputPath(source, batch.OutputName, format, appendExtension: true),
            (source, output) => [output],
            (source, output) => toZar
                ? (XisoZarchive.CreateZar(source, output) ? 0 : 1)
                : XisoZarchive.ConvertZarToIso(source, output, overwrite: true),
            CliOutputGuard.CheckImageOutput);
    }

    private static int RunSplitMode(string[] args, int optind)
    {
        string? sizeText = null;
        string? outputBase = null;
        bool assumeYes = false;
        bool assumeNo = false;
        BatchOptions batch = new();
        List<string> positionals = new();

        for (int i = optind; i < args.Length; i++)
        {
            string a = args[i];
            if (BatchConversion.TryConsume(args, ref i, batch, out string? batchFlagError))
            {
                if (batchFlagError != null)
                {
                    Logger.LogErr($"Error: {batchFlagError}\n");
                    return 1;
                }
            }
            else if (string.Equals(a, "--size", StringComparison.OrdinalIgnoreCase))
            {
                if (++i >= args.Length)
                {
                    Logger.LogErr("Error: --size requires a value (bytes, e.g. 732M, or 'half')\n");
                    PrintUsage();
                    return 1;
                }

                sizeText = args[i];
            }
            else if (string.Equals(a, "--output", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(a, "-o", StringComparison.OrdinalIgnoreCase))
            {
                if (++i >= args.Length)
                {
                    Logger.LogErr("Error: --output requires a base path\n");
                    PrintUsage();
                    return 1;
                }

                outputBase = args[i];
            }
            else if (string.Equals(a, "-y", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(a, "--yes", StringComparison.OrdinalIgnoreCase))
            {
                assumeYes = true;
            }
            else if (string.Equals(a, "-n", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(a, "--no", StringComparison.OrdinalIgnoreCase))
            {
                assumeNo = true;
            }
            else if (string.Equals(a, "-q", StringComparison.Ordinal))
            {
                Logger.Quiet = true;
            }
            else if (string.Equals(a, "-Q", StringComparison.OrdinalIgnoreCase))
            {
                Logger.Quiet = Logger.RealQuiet = true;
            }
            else if (string.Equals(a, "-h", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(a, "--help", StringComparison.OrdinalIgnoreCase))
            {
                PrintUsage();
                return 0;
            }
            else if (string.Equals(a, "-v", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(a, "--version", StringComparison.OrdinalIgnoreCase))
            {
                Console.Write(Constants.Banner);
                return 0;
            }
            else if (a.StartsWith('-'))
            {
                Logger.LogErr($"Error: unknown option for split: {a}\n");
                PrintUsage();
                return 1;
            }
            else
            {
                positionals.Add(a);
            }
        }

        bool halves = false;
        long partSize = XisoSplitter.DefaultPartSizeBytes;
        if (sizeText != null)
        {
            if (!TryParseSplitSize(sizeText, out partSize, out halves))
            {
                Logger.LogErr($"Error: invalid --size '{sizeText}' (bytes, K/M/G suffix, or 'half')\n");
                return 1;
            }

            if (!halves && partSize < Constants.SectorSize)
            {
                Logger.LogErr($"Error: --size must be at least one sector ({Constants.SectorSize} bytes)\n");
                return 1;
            }
        }

        if (assumeYes && assumeNo)
        {
            Logger.LogErr("[ERROR] Cannot use both --no (-n) and --yes (-y)\n");
            return 1;
        }

        string? batchError = BatchConversion.Validate(batch);
        if (batchError != null)
        {
            Logger.LogErr($"Error: {batchError}\n");
            return 1;
        }

        if (batch.Directory != null)
        {
            if (positionals.Count > 0 || outputBase != null)
            {
                Logger.LogErr("Error: --batch cannot be combined with explicit images or --output\n");
                return 1;
            }

            batch.AssumeYes = assumeYes;
            batch.AssumeNo = assumeNo;
            return RunSplitBatch(batch, partSize, halves);
        }

        if (positionals.Count == 0)
        {
            Logger.LogErr("Error: split requires at least one image\n");
            PrintUsage();
            return 1;
        }

        if (outputBase != null && positionals.Count > 1)
        {
            Logger.LogErr("Error: --output accepts a single base path; pass one image at a time\n");
            return 1;
        }

        int exit = 0;
        foreach (string iso in positionals)
        {
            try
            {
                string splitBase = outputBase
                                   ?? Path.Combine(Path.GetDirectoryName(Path.GetFullPath(iso)) ?? "",
                                       Path.GetFileNameWithoutExtension(iso));
                // CLI-008: prompt before clobbering existing parts (-y deletes the
                // confirmed colliders first, -n/refusal skips the image).
                List<string> collisions = FindExistingSplitParts(iso, splitBase, partSize, halves);
                bool refused = false;
                foreach (string collider in collisions)
                {
                    if (!OverwritePrompt.ConfirmOverwrite(collider, assumeYes, assumeNo))
                    {
                        Logger.LogErr($"Error splitting {iso}: not overwriting existing {collider}\n");
                        exit = 1;
                        refused = true;
                        break;
                    }
                }

                if (!refused && collisions.Count > 0)
                {
                    // The source probed valid inside FindExistingSplitParts, so
                    // removing the confirmed colliders cannot strand a bad split.
                    foreach (string collider in collisions)
                    {
                        try
                        {
                            File.Delete(collider);
                        }
                        catch (Exception ex)
                        {
                            Logger.LogErr($"Error splitting {iso}: cannot remove {collider}: {ex.Message}\n");
                            exit = 1;
                            refused = true;
                            break;
                        }
                    }
                }

                if (refused)
                    continue;

                IReadOnlyList<string> parts = halves
                    ? XisoReader.SplitXisoHalves(iso, splitBase)
                    : XisoReader.SplitXiso(iso, splitBase, partSize);
                foreach (string part in parts)
                    Logger.Log($"split: {part}\n");
            }
            catch (Exception ex)
            {
                Logger.LogErr($"Error splitting {iso}: {ex.Message}\n");
                exit = 1;
            }
        }

        return exit;
    }

    /// <summary>
    /// Phase 5: <c>split --batch</c>. Splits every unsplit ISO in the batch
    /// directory, reusing the shared skip/continue/parallel policy.
    /// </summary>
    private static int RunSplitBatch(BatchOptions batch, long partSize, bool halves)
    {
        List<string> sources;
        try
        {
            sources = BatchConversion.EnumerateSources(batch.Directory!, batch.Recursive,
                includeFirstPart: false, ".iso", ".xiso");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Logger.LogErr($"Error: {ex.Message}\n");
            return 1;
        }

        if (sources.Count == 0)
        {
            Logger.LogErr($"Error: no unsplit .iso/.xiso files found in {batch.Directory}\n");
            return 1;
        }

        return BatchConversion.Run(
            "split",
            batch,
            sources,
            source => BatchConversion.OutputPath(source, batch.OutputName, "iso", appendExtension: false),
            (source, output) => BatchConversion.SplitTargets(source, output, partSize, halves),
            (source, output) =>
            {
                // The runner confirmed every existing part before this point.
                foreach (string target in BatchConversion.SplitTargets(source, output, partSize, halves))
                {
                    if (File.Exists(target))
                        File.Delete(target);
                }

                IReadOnlyList<string> parts = halves
                    ? XisoReader.SplitXisoHalves(source, output)
                    : XisoReader.SplitXiso(source, output, partSize);
                foreach (string part in parts)
                    Logger.Log($"split: {part}\n");
                return 0;
            });
    }

    private static int RunJoinMode(string[] args, int optind)
    {
        string? outputPath = null;
        bool assumeYes = false;
        bool assumeNo = false;
        List<string> positionals = new();

        for (int i = optind; i < args.Length; i++)
        {
            string a = args[i];
            if (string.Equals(a, "--output", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(a, "-o", StringComparison.OrdinalIgnoreCase))
            {
                if (++i >= args.Length)
                {
                    Logger.LogErr("Error: --output requires a file path\n");
                    PrintUsage();
                    return 1;
                }

                outputPath = args[i];
            }
            else if (string.Equals(a, "-y", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(a, "--yes", StringComparison.OrdinalIgnoreCase))
            {
                assumeYes = true;
            }
            else if (string.Equals(a, "-n", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(a, "--no", StringComparison.OrdinalIgnoreCase))
            {
                assumeNo = true;
            }
            else if (string.Equals(a, "-q", StringComparison.Ordinal))
            {
                Logger.Quiet = true;
            }
            else if (string.Equals(a, "-Q", StringComparison.OrdinalIgnoreCase))
            {
                Logger.Quiet = Logger.RealQuiet = true;
            }
            else if (string.Equals(a, "-h", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(a, "--help", StringComparison.OrdinalIgnoreCase))
            {
                PrintUsage();
                return 0;
            }
            else if (string.Equals(a, "-v", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(a, "--version", StringComparison.OrdinalIgnoreCase))
            {
                Console.Write(Constants.Banner);
                return 0;
            }
            else if (a.StartsWith('-'))
            {
                Logger.LogErr($"Error: unknown option for join: {a}\n");
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
            Logger.LogErr("Error: join requires at least one *.1.iso part\n");
            PrintUsage();
            return 1;
        }

        if (outputPath != null && positionals.Count > 1)
        {
            Logger.LogErr("Error: --output accepts a single file; pass one part set at a time\n");
            return 1;
        }

        int exit = 0;
        foreach (string first in positionals)
        {
            try
            {
                // CLI-007: require the .1.iso suffix instead of slicing blindly
                // (short/foreign names threw ArgumentOutOfRangeException).
                if (!XisoSplitter.IsSplitPath(first))
                {
                    Logger.LogErr($"Error joining {first}: expected the first split part (*.1.iso)\n");
                    exit = 1;
                    continue;
                }

                string joined = outputPath
                                ?? Path.ChangeExtension(first[..^".1.iso".Length], ".iso");
                // CLI-008: prompt before clobbering an existing output.
                if (File.Exists(joined))
                {
                    if (!File.Exists(first))
                    {
                        Logger.LogErr($"Error joining {first}: split part not found\n");
                        exit = 1;
                        continue;
                    }

                    if (!OverwritePrompt.ConfirmOverwrite(joined, assumeYes, assumeNo))
                    {
                        Logger.LogErr($"Error joining {first}: not overwriting existing {joined}\n");
                        exit = 1;
                        continue;
                    }

                    try
                    {
                        File.Delete(joined);
                    }
                    catch (Exception ex)
                    {
                        Logger.LogErr($"Error joining {first}: cannot remove {joined}: {ex.Message}\n");
                        exit = 1;
                        continue;
                    }
                }

                XisoReader.JoinSplitXiso(first, joined);
                Logger.Log($"join: {joined}\n");
            }
            catch (Exception ex)
            {
                Logger.LogErr($"Error joining {first}: {ex.Message}\n");
                exit = 1;
            }
        }

        return exit;
    }

    /// <summary>
    /// Parses a <c>--size</c> value for split mode: a plain byte count with an
    /// optional K/M/G (binary) suffix, or <c>half</c>/<c>halves</c>.
    /// </summary>
    private static bool TryParseSplitSize(string text, out long bytes, out bool halves)
    {
        bytes = 0;
        halves = false;
        if (string.Equals(text, "half", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(text, "halves", StringComparison.OrdinalIgnoreCase))
        {
            halves = true;
            return true;
        }

        long multiplier = 1;
        string digits = text;
        if (text.Length > 1 && char.IsAsciiLetter(text[^1]))
        {
            multiplier = char.ToUpperInvariant(text[^1]) switch
            {
                'K' => 1024L,
                'M' => 1024L * 1024,
                'G' => 1024L * 1024 * 1024,
                _ => 0,
            };
            if (multiplier == 0)
                return false;
            digits = text[..^1];
        }

        if (!long.TryParse(digits, CultureInfo.InvariantCulture, out long count) || count <= 0)
            return false;
        try
        {
            bytes = checked(count * multiplier);
        }
        catch (OverflowException)
        {
            return false;
        }

        return bytes > 0;
    }

    private static string DeriveRedumpPath(string xisoPath)
    {
        string dir = Path.GetDirectoryName(xisoPath) ?? "";
        string full = Path.GetFileName(xisoPath);
        string baseName = full;
        if (full.EndsWith(".xiso", StringComparison.OrdinalIgnoreCase))
        {
            baseName = full[..^5];
        }
        else if (full.EndsWith(".zar", StringComparison.OrdinalIgnoreCase) ||
                 full.EndsWith(".iso", StringComparison.OrdinalIgnoreCase))
        {
            baseName = full[..^4];
        }

        return Path.Combine(dir, baseName + ".redump.iso");
    }

    private static int RunRedumpBatch(List<string> isoFiles, bool video, bool random, bool seed, bool wipe, bool trim,
        bool petrify, bool update, bool zar, string? outputName,
        bool assumeYes, bool assumeNo, int jobs = 1, ZarCollisionPolicy? policy = null)
    {
        // Single-output guard
        bool singleModeCount = new[] { video, random, seed, wipe, trim, petrify, update, zar }.Count(b => b) == 1;
        // CLI-014: -o with several redump modes used to skip the single-input
        // guard below and then be ignored per-file. Fail loudly instead.
        if (outputName != null && !singleModeCount)
        {
            Logger.LogErr("Error: -o <output> requires exactly one redump mode and a single input file\n");
            return 1;
        }

        if (outputName != null && isoFiles.Count != 1 && singleModeCount)
        {
            Logger.LogErr("Error: -o <output> can only be used with a single input file\n");
            return 1;
        }

        // #15 (xdvdfs #36): a single-input -o must not point back at the input.
        if (outputName != null && singleModeCount && isoFiles.Count == 1)
        {
            string? batchRefusal = CliOutputGuard.CheckSingleInputOutput(isoFiles[0], outputName);
            if (batchRefusal != null)
            {
                Logger.LogErr(batchRefusal);
                return 1;
            }
        }

        int exit = 0;

        // Container inputs are not raw sector images. Refuse them before any
        // mode touches the file so a .cci/.cso/.zar/split set gets a clear
        // "decompress/convert/join first" message instead of a TOC parse error.
        List<string> supportedInputs = new(isoFiles.Count);
        foreach (string iso in isoFiles)
        {
            string? refusal = RedumpInputRefusal(iso);
            if (refusal is null)
            {
                supportedInputs.Add(iso);
                continue;
            }

            if (singleModeCount)
            {
                Logger.LogErr($"Error: redump operations require a plain ISO; {refusal}\n");
                exit = 1;
            }
            else
            {
                Logger.Log($"[INFO] Skipping {iso}: {refusal}\n");
            }
        }

        if (supportedInputs.Count == 0) return exit;
        isoFiles = supportedInputs;

        // ZarManager-style batch: a lone --zar over several inputs with an
        // explicit (non-interactive) policy packs in parallel.
        if (zar && singleModeCount && policy.HasValue && jobs > 1 && isoFiles.Count > 1)
        {
            int failures = 0;
            Parallel.ForEach(isoFiles, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, jobs) }, iso =>
            {
                if (DeriveAndPackZar(iso, policy.Value) != 0)
                    Interlocked.Exchange(ref failures, 1);
            });
            return failures != 0 ? 1 : exit;
        }

        foreach (string iso in isoFiles)
        {
            long size;
            try
            {
                size = new FileInfo(iso).Length;
            }
            catch
            {
                Logger.LogErr($"Cannot stat {iso}\n");
                exit = 1;
                continue;
            }

            int redumpType = XgdTables.GetRedumpIsoTypeBySize(size);
            int vidType = XgdTables.GetVideoTypeBySize(size);
            bool isRedump = redumpType >= 0;
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
                    using FileStream fs = new(iso, FileMode.Open, FileAccess.Read, FileShare.Read, 65536);
                    videoTypeForRedump = XgdTables.GetVideoType(fs, redumpType);
                }
                catch
                {
                    // ignored
                }

                int vType = videoTypeForRedump >= 0 ? videoTypeForRedump : 0;
                int xsType = XgdTables.GetXisoTypeFromVideo(vType);
                if (xsType < 0 || xsType >= XgdTables.XisoOffset.Length)
                    xsType = redumpType >= 0 ? XgdTables.GetXgdType(redumpType) : 0;
                isoOffset = XgdTables.XisoOffset[xsType];
                xisoLen = XgdTables.XisoLength[xsType];
            }

            string dir = Path.GetDirectoryName(iso) ?? "";
            string full = Path.GetFileName(iso);
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
                    if (!ConfirmOutput(outVideo, $"--video for {iso}"))
                    {
                        // Skipped by user choice (-n refusal already failed this file above).
                    }
                    else if (!XisoRedump.TryExtractVideo(iso, outVideo, out string? outPath, Logger.Quiet))
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
                    if (!ConfirmOutput(outUpd, $"--update for {iso}"))
                    {
                        // Skipped by user choice (-n refusal already failed this file above).
                    }
                    else if (!XisoRedump.TryExtractUpdate(iso, outUpd, wipe: true, quiet: Logger.Quiet))
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
                        if (!ConfirmOutput(outUpd, $"--update for {iso}"))
                        {
                            // Skipped by user choice (-n refusal already failed this file above).
                        }
                        else if (!XisoRedump.TryExtractUpdate(videoPath, outUpd, wipe: true, quiet: Logger.Quiet))
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
                // A declined output counts as handled (skipped) so later ops still run.
                bool fillerConfirmed = ConfirmOutput(outFiller, $"--random for {iso}");
                bool ok = !fillerConfirmed || (isRedump
                    ? XisoOperations.ExtractFiller(iso, outFiller, isoOffset, xisoLen, Logger.Quiet)
                    : XisoOperations.ExtractFiller(iso, outFiller, 0, null, Logger.Quiet));
                if (!ok)
                {
                    Logger.LogErr($"[ERROR] Failed extracting filler from {iso}\n");
                    exit = 1;
                }
                else if (fillerConfirmed)
                {
                    Logger.Log($"Filler extracted to {outFiller} ({new FileInfo(outFiller).Length} bytes)\n");
                }
            }

            if (seed)
            {
                string outSeed = (outputName != null && singleModeCount)
                    ? outputName
                    : Path.Combine(dir, baseName + ".seed");
                // A declined output counts as handled (skipped) so later ops still run;
                // the failure cleanup below must not touch a file we refused to overwrite.
                bool seedConfirmed = ConfirmOutput(outSeed, $"--seed for {iso}");
                bool ok = !seedConfirmed ||
                          XisoOperations.TryExtractSeed(iso, outSeed, isRedump ? isoOffset : 0, Logger.Quiet);
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
                else if (seedConfirmed)
                {
                    Logger.Log($"Seed extracted to {outSeed}\n");
                }
            }

            bool wipeDeclined = false;

            if (wipe)
            {
                string outWiped = (outputName != null && singleModeCount)
                    ? outputName
                    : Path.Combine(dir, baseName + ".wiped.xiso");
                // A declined output counts as handled (skipped) so later ops still run.
                bool wipeConfirmed = ConfirmOutput(outWiped, $"--wipe for {iso}");
                wipeDeclined = !wipeConfirmed;
                bool ok = !wipeConfirmed || (isRedump
                    ? XisoOperations.WipeFiller(iso, outWiped, isoOffset, Logger.Quiet)
                    : XisoOperations.WipeFiller(iso, outWiped, 0, Logger.Quiet));

                if (!ok)
                {
                    Logger.LogErr($"[ERROR] Failed wiping {iso}\n");
                    exit = 1;
                }
                else if (wipeConfirmed)
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
                    // If we already produced wiped, trim it; else do combined.
                    // A declined --wipe must not be consumed+deleted here: fall through to
                    // the combined op (which confirms its own output) instead.
                    if (File.Exists(wipedPath) && !singleModeCount && !wipeDeclined)
                    {
                        // Trim the already-wiped file
                        if (!ConfirmOutput(outTrimWiped, $"--trim for {iso}"))
                        {
                            // Skipped by user choice (-n refusal already failed this file above).
                        }
                        else if (!XisoOperations.TrimXiso(wipedPath, outTrimWiped, 0, Logger.Quiet))
                        {
                            Logger.LogErr($"[ERROR] Failed trimming {wipedPath}\n");
                            exit = 1;
                        }
                        else
                        {
                            try
                            {
                                File.Delete(wipedPath);
                            }
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
                        // Do combined directly. A declined output counts as handled
                        // (skipped); the separate-trim branch below is the outer
                        // else and is not reached from here (Todo #30).
                        bool trimConfirmed = ConfirmOutput(outPath2, $"--trim for {iso}");
                        bool ok = !trimConfirmed ||
                                  XisoOperations.WipeAndTrim(iso, outPath2, isRedump ? isoOffset : 0, Logger.Quiet);
                        if (!ok)
                        {
                            Logger.LogErr($"[ERROR] Failed wiping+trimming {iso}\n");
                            exit = 1;
                        }
                        else if (trimConfirmed)
                        {
                            Logger.Log($"Wiped+trimmed XISO written to {outPath2}\n");
                        }
                    }
                }
                else
                {
                    string outTrim = (outputName != null && singleModeCount)
                        ? outputName
                        : Path.Combine(dir, baseName + ".trim.xiso");
                    // A declined output counts as handled (skipped) so later ops still run.
                    bool trimConfirmed = ConfirmOutput(outTrim, $"--trim for {iso}");
                    bool ok = !trimConfirmed ||
                              XisoOperations.TrimXiso(iso, outTrim, isRedump ? isoOffset : 0, Logger.Quiet);
                    if (!ok)
                    {
                        Logger.LogErr($"[ERROR] Failed trimming {iso}\n");
                        exit = 1;
                    }
                    else if (trimConfirmed)
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
                if (!ConfirmOutput(outSkel, $"--petrify for {iso}") ||
                    !ConfirmOutput(outHash, $"--petrify hash for {iso}"))
                {
                    // Skipped by user choice (-n refusal already failed this file above).
                }
                else if (!XisoSkeleton.Petrify(iso, outSkel, outHash, isRedump ? isoOffset : 0,
                             isRedump ? xisoLen : null, Logger.Quiet))
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
                if (policy == null)
                {
                    if (!ConfirmOutput(outZar, $"--zar for {iso}"))
                    {
                        // Skipped by user choice (-n refusal already failed this file above).
                    }
                    else if (!XisoZarchive.CreateZar(iso, outZar, isRedump ? isoOffset : 0, Logger.Quiet))
                    {
                        Logger.LogErr($"[ERROR] Failed creating ZAR for {iso}\n");
                        exit = 1;
                    }
                    else
                    {
                        Logger.Log($"ZAR written to {outZar}\n");
                    }
                }
                else if (PackZarResolved(iso, outZar, isRedump ? isoOffset : 0, policy.Value, parallel: false) != 0)
                {
                    exit = 1;
                }
            }
        }

        return exit;

        // Non-interactive --zar for one ISO: resolve the output under the
        // collision policy, then pack. Returns 0 on success or skip, 1 on
        // failure. Parallel workers stay quiet; one summary line is logged
        // under a lock so batch output does not interleave.
        static int DeriveAndPackZar(string isoPath, ZarCollisionPolicy pol)
        {
            long size;
            try
            {
                size = new FileInfo(isoPath).Length;
            }
            catch
            {
                lock (LogLock)
                {
                    Logger.LogErr($"Cannot stat {isoPath}\n");
                }

                return 1;
            }

            int redumpType = XgdTables.GetRedumpIsoTypeBySize(size);
            long offset = 0;
            if (redumpType >= 0)
            {
                int xsType = XgdTables.GetXgdType(redumpType);
                if (xsType >= 0 && xsType < XgdTables.XisoOffset.Length)
                    offset = XgdTables.XisoOffset[xsType];
            }

            string isoDir = Path.GetDirectoryName(isoPath) ?? "";
            string full = Path.GetFileName(isoPath);
            string stem = full;
            if (full.EndsWith(".redump.iso", StringComparison.OrdinalIgnoreCase))
                stem = full[..^".redump.iso".Length];
            else if (full.EndsWith(".video.iso", StringComparison.OrdinalIgnoreCase))
                stem = full[..^".video.iso".Length];
            else if (full.EndsWith(".iso", StringComparison.OrdinalIgnoreCase)) stem = full[..^".iso".Length];
            else if (full.EndsWith(".xiso", StringComparison.OrdinalIgnoreCase)) stem = full[..^".xiso".Length];
            return PackZarResolved(isoPath, Path.Combine(isoDir, stem + ".zar"), offset, pol, parallel: true);
        }

        static int PackZarResolved(string isoPath, string outZar, long isoOffset, ZarCollisionPolicy pol, bool parallel)
        {
            string? resolved;
            try
            {
                resolved = ZarPackEngine.ResolveOutputPath(outZar, pol);
            }
            catch (IOException)
            {
                Logger.LogErr($"[ERROR] Output file already exists: {outZar}\n");
                return 1;
            }

            if (resolved == null)
            {
                if (parallel)
                {
                    lock (LogLock)
                    {
                        Logger.Log($"[INFO] Skipping --zar for {isoPath}: output exists\n");
                    }
                }
                else
                {
                    Logger.Log($"[INFO] Skipping --zar for {isoPath}: output exists\n");
                }

                return 0;
            }

            bool ok;
            try
            {
                ok = XisoZarchive.CreateZar(isoPath, resolved, isoOffset, quiet: parallel || Logger.Quiet);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                if (parallel)
                {
                    lock (LogLock)
                    {
                        Logger.LogErr($"[ERROR] Failed creating ZAR for {isoPath}: {ex.Message}\n");
                    }
                }
                else
                {
                    Logger.LogErr($"[ERROR] Failed creating ZAR for {isoPath}: {ex.Message}\n");
                }

                return 1;
            }

            if (!ok)
            {
                if (parallel)
                {
                    lock (LogLock)
                    {
                        Logger.LogErr($"[ERROR] Failed creating ZAR for {isoPath}\n");
                    }
                }
                else
                {
                    Logger.LogErr($"[ERROR] Failed creating ZAR for {isoPath}\n");
                }

                return 1;
            }

            if (parallel)
            {
                lock (LogLock)
                {
                    Logger.Log($"ZAR written to {resolved}\n");
                }
            }
            else
            {
                Logger.Log($"ZAR written to {resolved}\n");
            }

            return 0;
        }

        // -y/--yes/-n/--no gate for every file this batch writes (XboxKit ConfirmOverwrite
        // parity). A declined output skips just that operation; a -n refusal also fails
        // the file (the [ERROR] line is printed by the helper itself).
        bool ConfirmOutput(string outPath, string what)
        {
            if (OverwritePrompt.ConfirmOverwrite(outPath, assumeYes, assumeNo))
                return true;
            if (assumeNo)
                exit = 1;
            else
                Logger.Log($"[INFO] Skipping {what}: output exists\n");
            return false;
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
        bool xbeInfoMode,
        bool repairMode,
        bool salvageMode,
        bool unpackMode,
        bool hashMode,
        bool copyOut,
        bool copyIn,
        bool auditMode,
        bool validateMode,
        bool tree,
        bool extract,
        bool checksumFlagMode,
        bool filetimeMode,
        bool setFiletimeMode,
        bool sectorLayoutMode,
        bool rangesMode,
        bool isOptimizedMode,
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

        if (rewrite || info || lsMode || xexInfoMode || xbeInfoMode || repairMode || salvageMode || unpackMode ||
            hashMode || copyOut || copyIn || auditMode ||
            validateMode || tree || !extract || checksumFlagMode || filetimeMode || setFiletimeMode ||
            sectorLayoutMode || rangesMode || isOptimizedMode)
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
                EnumerationOptions options = new()
                {
                    MatchCasing = MatchCasing.CaseInsensitive,
                    RecurseSubdirectories = batchRecursive,
                    AttributesToSkip = FileAttributes.None // include hidden files, like the SearchOption overload
                };
                List<string> isoFiles = Directory.EnumerateFiles(batchDir, "*.iso", options)
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

        List<string> files = args.Skip(optind).ToList();
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
        List<string> parts = new();
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
        List<string> parts = new();
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
    private static string FormatXexEncryption(ushort encryption) =>
        encryption switch
        {
            0 => "None",
            1 => "Normal",
            _ => "Unknown"
        };

    /// <summary>Formats the XEX compression type as a name.</summary>
    private static string FormatXexCompression(ushort compression) =>
        compression switch
        {
            0 => "None",
            1 => "Basic",
            2 => "Normal",
            3 => "Delta",
            _ => "Unknown"
        };

    /// <summary>
    /// Formats the XBE allowed-media bitmask as a comma-separated list of names.
    /// </summary>
    private static string FormatXbeMedia(uint media)
    {
        List<string> parts = new();
        if ((media & 0x01) != 0) parts.Add("HardDisk");
        if ((media & 0x02) != 0) parts.Add("DvdCd");
        if ((media & 0x04) != 0) parts.Add("Dvd5Ro");
        if ((media & 0x08) != 0) parts.Add("Dvd9Ro");
        if ((media & 0x10) != 0) parts.Add("Dvd5Rw");
        if ((media & 0x20) != 0) parts.Add("Dvd9Rw");
        if ((media & 0x40) != 0) parts.Add("Dongle");
        if ((media & 0x80) != 0) parts.Add("MediaBoard");
        return parts.Count > 0 ? $" ({string.Join(", ", parts)})" : "";
    }

    /// <summary>
    /// Formats the XBE game-region bitmask as a comma-separated list of names.
    /// </summary>
    private static string FormatXbeRegion(uint region)
    {
        List<string> parts = new();
        if ((region & 0x01) != 0) parts.Add("NorthAmerica");
        if ((region & 0x02) != 0) parts.Add("Japan");
        if ((region & 0x04) != 0) parts.Add("RestOfWorld");
        return parts.Count > 0 ? $" ({string.Join(", ", parts)})" : "";
    }

    /// <summary>
    /// Formats a directory entry attribute byte as a human-readable string.
    /// </summary>
    private static string FormatAttributes(byte attrs)
    {
        List<string> parts = new();
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
    private static void PrintUsage() =>
        Console.Error.Write(Constants.Banner + """
                                                 Usage:

                                                    XISOSharp [options] [-[lrx]] <file1.xiso> [file2.xiso] ...
                                                    XISOSharp [options] -c <dir> [name] [-c <dir> [name]] ...

                                                   Mutually exclusive modes:

                                                     -c <dir> [name]     Create xiso from file(s) starting in <dir>.
                                                      --copy-out <iso> <path> <dest>  Copy a file or directory out of an xiso.
                                                      --copy-in <iso> <host> <path>  Copy a host file into an xiso
                                                                            (replace or add; writes <iso>.old backup
                                                                            unless --no-backup).
                                                     -i <file> [path]    Show volume info and directory entry metadata.
                                                     --ls <file> [path]   List the entries of a directory (default root)
                                                                           without recursion. Mirrors 'ls' on the image.
                                                     --xex-info <file> <path>  Show the Xbox 360 XEX2 executable
                                                                           header of a .xex file inside the image
                                                                           (module flags, entry point, title ID, ...).
                                                     --xbe-info <file> <path>  Show the original-Xbox XBEH executable
                                                                           header + certificate of a .xbe file inside
                                                                           the image (title ID/name, media, region).
                                                     -l                  List files in xiso(s).
                                                     --md5 <file> [path] Compute MD5 hash of file(s) in xiso.
                                                     -r                  Rewrite xiso(s) as optimized xiso(s).
                                                     --sha256 <file> [path] Compute SHA-256 hash of file(s) in xiso.
                                                      -t                  List all files recursively with sizes (tree).
                                                      -V <file1.xiso> ...  Deep-audit xiso(s): validate header, tree,
                                                                           sectors, and attributes; the optimized tag
                                                                           is reported but not required (see
                                                                           --is-optimized).
                                                     --repair <file>     Repair fixable issues in place (reserved
                                                                           attribute bits, missing optimized tag,
                                                                           separators in names); writes <file>.old
                                                                           backup unless --no-backup.
                                                      --dry-run           With --repair, preview fixes without changing
                                                                            anything (rejected without --repair).
                                                      --salvage <file>    Rebuild a readable image from a corrupt
                                                                            one: carry every reachable entry into
                                                                            <file> without its extension plus
                                                                            '.salvaged.iso' (source never modified;
                                                                            CISO input allowed). --repair-out
                                                                            overrides the output path (existing
                                                                            files follow the -y/-n convention).
                                                      --filetime <image>   Show FILETIME header field (ISO-8601 + raw u64, xdvdfs compatible; 0 = 1601-01-01).
                                                      --set-filetime <image> <value>  Set FILETIME field: value may be ISO-8601 (2023-08-26T15:00:00Z), decimal raw, 0x hex, 'now', or '0'.
                                                      --batch <dir>        Process all .iso files in <dir> instead of
                                                                           explicit filenames (extract, list, tree, rewrite
                                                                           (-r), audit (-V)); on compress/decompress/convert/split,
                                                                           the verb's own inputs.
                                                     --batch-recursive    With --batch, search subdirectories recursively.
                                                     --pack <input> [name]  Pack a directory into an ISO (name defaults
                                                                           to the directory name; may include a path),
                                                                           or repack an existing ISO in place (rewrite).
                                                     validate <src> <out> Validate conversion by comparing source and output ISOs.
                                                     -x                  Extract xiso(s) (the default mode if none is given).
                                                     --unpack <file> [dest]  Unpack the whole image to <dest>, or to a
                                                                           directory named after the ISO when omitted.
                                                                           -l/-t/-x/--unpack/checksum accept .zar
                                                                           archives directly (ZArchive name tree).
                                                     -X <glob_pattern>   In create mode (-c), exclude files/directories
                                                                           matching the glob pattern from the image.
                                                                           Repeatable. Examples: "*.tmp", "**/node_modules/**",
                                                                           "screenshots/**". Use "/" as the path separator.
                                                                           With -s, $SystemUpdate is excluded automatically.

                                                     Redump / XboxKit modes (lossless archival):

                                                      rebuild <xiso|game.zar> [video.iso] [filler|seed] [su...] -o <redump.iso>
                                                                            Rebuild a Redump ISO from its components.
                                                                            <xiso> may be a .zar sidecar (single embedded
                                                                            XISO used verbatim, else tree repacked).
                                                                            Auto-detects XGD type by size; video type via
                                                                           PVD at 0x832D. Supports filler file or 4-byte
                                                                           seed (XGD1 PRNG) plus optional sectors.txt for
                                                                           security sectors (--security-sectors).
                                                     --video <redump.iso> -o <video.iso>  Extract video partition (L0+L1).
                                                     --random <iso> -o <filler.bin>  Extract random filler gaps.
                                                     --seed <iso> -o <seed.bin>  Extract XGD1 PRNG seed (brute-force, 4 bytes).
                                                     --wipe <iso> -o <wiped.xiso>  Write XISO with filler zeroed.
                                                     --trim <iso> -o <trimmed.xiso>  Trim XISO after last file extent.
                                                     --petrify <iso> [-o <skeleton.xiso>]  Zero file data, emit SHA1 hashes (.hash alongside).
                                                     --update <video.iso|redump.iso> [-o <update>]  Extract su20076000_00000000 (XGD3) and zero it in video.
                                                     --zar <iso> -o <out.zar>   Create ZArchive (zstd blocks, raw fallback; trimmable).
                                                                                -o is only valid with exactly one mode and
                                                                                one input; without it each mode derives
                                                                                its own output name.
                                                     --jobs <n>               With a lone --zar over several inputs, pack
                                                                            up to <n> archives in parallel (default 1).
                                                     --policy <p>            --zar overwrite handling without prompting:
                                                                            skip | overwrite | auto-rename.
                                                     --all <redump.iso>         Alias: --random --seed --trim --update --video --wipe
                                                     --best <iso>               Alias: --trim --wipe
                                                     --compress <iso>           Alias: --petrify --update --video --zar
                                                      --security-sectors <sectors.txt>  Override security sector ranges (start-end, 4096 sectors).

                                                      CISO / CCI / Compression modes:

                                                      compress <sourceDir|image> [output] [--format cso|cci]
                                                               [--ciso-level 0..9] [--ciso-version 1|2|auto] [--ciso-split bytes]
                                                               [--cci-level 0..9] [--cci-split bytes] [--batch <dir>]
                                                                            Pack and compress to CISO/CSO (default) or CCI.
                                                                            Source may be a directory (packed then compressed),
                                                                            an ISO, or a split ISO/CISO/CCI image (decompressed
                                                                            to a temporary ISO first).
                                                                            Level 0=store, 1=fastest … 9=smallest. Default 9.
                                                                            CISO version 2 (default) writes LZ4 sectors with fixed
                                                                            align 2 — byte-compatible with modern `xdvdfs compress`;
                                                                            version 1 writes classic DEFLATE sectors.
                                                                            Like xdvdfs, CISO output is split at 0xffbf6000
                                                                            (~4 GiB) into .1.cso/.2.cso parts; --ciso-split bytes
                                                                            overrides the point, --ciso-split 0 writes a single .cso.
                                                                            CCI output is split at 0xffbf6000 into .1.cci/.2.cci …
                                                                            parts; --cci-split 0 writes a single .cci.
                                                                            Aliases: cso (CISO), cci (CCI).
                                                      decompress <cso|cci> [output.iso] [--batch <dir>]    Decompress CISO/CSO or CCI to ISO (DEFLATE v1,
                                                                            LZ4 v2 and CCI LZ4; single files and split parts).
                                                                            Aliases: uncso, decso, uncci.
                                                       checksum <image> [images...] [--silent]  Compute SHA3-256 image checksum (xdvdfs-compatible:
                                                                             SHA3-256 over sorted path bytes + file data). Output: hex tab path.
                                                       split <image> [images...] [--size <bytes|half>] [--output <base>] [--batch <dir>]  Split ISO into sector-aligned
                                                                             .1.iso/.2.iso… parts (default size 4G FATX cap; 'half' splits in two).
                                                       join <first.1.iso> [...] [--output <file>]  Reassemble split parts (output validated as XISO).
                                                       convert <src.zar|src.iso> <out.iso|out.zar>  Convert ZArchive to ISO (extract + repack)
                                                                             or ISO to ZArchive. Existing outputs follow -y/-n.
                                                                             --batch <dir> --format iso|zar converts a whole
                                                                             directory (default format: zar).
                                                       Batch container verbs (compress/decompress/convert/split):
                                                       --skip-existing      With --batch, skip sources whose output exists.
                                                       --continue-on-error  With --batch, keep going after a failed file
                                                                            and exit 1 after the summary.
                                                       --jobs <n>           With --batch, run up to <n> conversions in
                                                                            parallel (1..64; default 1).
                                                       --output-name <t>    With --batch, name outputs from a template:
                                                                            {name} (source stem, split marker stripped),
                                                                            {ext} (source extension), {format} (output format).
                                                                            Example: "{name}.xiso.{format}". A template
                                                                            without an extension gets the format's extension.
                                                       --filetime <image>             Show FILETIME header field (human-readable + raw, supports --skip-sectors).
                                                       --set-filetime <image> <value> Set FILETIME header field (value: ISO-8601, decimal raw, 0x hex, 'now', '0').
                                                       --sector-layout <image>      Show the full sector map: volume summary, per-file extents, used/free ranges.
                                                       --ranges <image>             List system (bone) vs file sector ranges (inclusive spans).
                                                       --is-optimized <image>       Print whether the image carries the optimized tag (supports --skip-sectors).

                                                       XDVDFS / Packing modes (ordered remapping):

                                                      build-image [sourceDir] [output.iso] -f <xdvdfs.toml> -m "hostGlob:imagePath" [-O output] [-D|--dry-run] [--file-time <value>] [--layout <file>]
                                                                            Pack an image with ordered wax-glob remapping ({0} whole match, {1..n} per '*'/'**' capture, '!' exclusion). Examples:
                                                                              build-image -m "bin:/" -m "assets/**:/assets/{1}" ./src dist/final.xiso.iso
                                                                              build-image --dry-run -f xdvdfs.toml ./src
                                                                            Globs support '*', '?', '**', '[]', '{a,b}'. Order matters; first match wins, negation clears and allows re-inclusion.
                                                                            In -m rules the separator is the first unescaped ':' — write '\:' for a literal colon, '\\' for a backslash, and drive prefixes like C:/... are kept (TOML -f needs no escaping).
                                                                            When -m is used, -f is ignored; -O sets output when no positional is given. Dry-run prints "host -> image".
                                                      image-spec from -O <out> -m <host:image> ... [specPath]
                                                                            Generate an xdvdfs.toml from map rules (stdout when omitted, file when given). Example:
                                                                              image-spec from -O dist/image.xiso.iso -m "bin:/" -m "assets/**:/{0}" xdvdfs.toml

                                                    Options:

                                                    -d <directory>      In extract mode, expand xiso in <directory>.
                                                                        In rewrite mode, rewrite xiso in <directory>.
                                                    -D                  In rewrite mode, delete old xiso after processing.
                                                    -h, --help         Print this help text and exit.
                                                    -n, --no            Never overwrite: refuse when an output file
                                                                          exists (rebuild, rewrite -o, compress,
                                                                          decompress, redump batch outputs).
                                                    -y, --yes           Always overwrite without prompting.
                                                    -m                  In create or rewrite mode, disable automatic .xbe
                                                                          media enable patching (not recommended).
                                                    -o <filename>       In rewrite mode, set custom output filename
                                                                          (default: original name with .iso extension).
                                                     --skip-sectors N     Treat the image as if the XISO filesystem
                                                                           begins N sectors (2048 bytes each) into the
                                                                           file. Use for Redump images where a video
                                                                           partition precedes the game partition.
                                                                           Valid in extract, list, tree, rewrite, unpack, and filetime modes.
                                                     --prepend-sectors N  Write the output image with N empty sectors
                                                                           before the XISO filesystem, leaving room for
                                                                           a video partition. Valid in create (-c) and
                                                                           rewrite (-r) mode. Combine with --skip-sectors
                                                                           for round-trip Redump-style reconstruction.
                                                     --layout <file>      Create (-c), --pack <dir> and build-image:
                                                                           place matching files/directories at the sectors
                                                                           from a --sector-layout --json file or a compact
                                                                           "path=sector" text spec; other entries are
                                                                           allocated first-fit around them. Overlaps,
                                                                           too-small extents and out-of-range sectors fail.
                                                     --preserve-attrs     In rewrite (-r) mode, re-encode the source
                                                                           dirent attribute bits (RO/HID/SYS/NOR) into
                                                                           the output. Default is extract-xiso parity:
                                                                           entries are re-encoded with the DIR/ARC
                                                                           defaults (dir=0x10, file=0x20) so rewritten
                                                                           images are byte-identical to extract-xiso.
                                                     --file-time <value>  Fixed FILETIME for the volume descriptor
                                                                          (value: ISO-8601, decimal raw, 0x hex,
                                                                          'now', '0'). '0' writes the xdvdfs
                                                                          deterministic timestamp so identical input
                                                                          produces byte-identical output. Valid in
                                                                          create (-c), --pack, and build-image modes.
                                                     --skip-existing      In extract, --unpack, and --copy-out modes,
                                                                          skip files already on disk with matching
                                                                          sizes (logged as "skip: <path>") instead of
                                                                          overwriting them. Re-run an interrupted
                                                                          unpack to resume it. Pairs with --batch so
                                                                          an interrupted bulk run resumes instead of
                                                                          redoing completed files.
                                                     --continue-on-error  In extract, --unpack, and --copy-out modes,
                                                                          log per-file failures (uncreatable output,
                                                                          truncated data) as "Error: Failed to
                                                                          extract ..." and continue with the next
                                                                          entry instead of aborting. An uncreatable
                                                                          directory skips its subtree. The run still
                                                                          ends with a "Failed to unpack image"
                                                                          summary and a non-zero exit code.
                                                    -q                  Run quiet (suppress all non-error output).
                                                    -Q                  Run silent (suppress all output).
                                                    -s                  Skip $SystemUpdate folder.
                                                    -v, --version       Print version information (XISOSharp, CCISharp,
                                                                        ZArchiveSharp, runtime, commit) and exit.
                                                    --json              Machine-readable output for read-only modes
                                                                        (-i, -l, -t, --checksum, --sector-layout,
                                                                        --ranges, --filetime, --xex-info, --xbe-info,
                                                                        validate, -v/--version). JSON goes to stdout;
                                                                        human logs go to stderr. List/tree output is
                                                                        {"schemaVersion":1,"command":"list","images":[...]};
                                                                        checksum output is a "checksums" array.

                                                    Validation options (with -r or validate command):

                                                    --validate          Enable post-conversion validation after rewrite.
                                                    --validate-checksums  Also verify SHA-256 checksums (slower).
                                                    --validate-strict   Retained for parity: mismatches exit 2 with
                                                                        or without it. Implies --validate.
                                                    --validate-report <file>  Write JSON validation report to file.

                                               """);
}
