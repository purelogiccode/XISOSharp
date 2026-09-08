namespace XISOSharp.BattleTests;

/// <summary>Parsed command-line options for the CLI-vs-CLI battle harness.</summary>
internal sealed class BattleOptions
{
    /// <summary>Gets dirs scanned (top-level) for candidate *.iso files.</summary>
    public List<string> Dirs { get; init; } = [];

    /// <summary>Gets how many ISOs are sampled at random (default 3).</summary>
    public int Count { get; init; } = 3;

    /// <summary>Gets the explicit RNG seed; null = auto (reported for reproducibility).</summary>
    public int? Seed { get; init; }

    /// <summary>Gets an explicit path to the XISOSharp CLI exe (default: auto-resolved).</summary>
    public string? CliPath { get; init; }

    /// <summary>Gets an explicit path to extract-xiso.exe (default: beside the harness).</summary>
    public string? OraclePath { get; init; }

    /// <summary>Gets an explicit path to xdvdfs.exe (default: beside the harness); null = auto-resolve.</summary>
    public string? XdvdfsPath { get; init; }

    /// <summary>Gets an explicit path to xboxkit.exe (default: beside the harness); null = auto-resolve.</summary>
    public string? XboxkitPath { get; init; }

    /// <summary>Gets the work root for scratch dirs (default: %TEMP%\xiso_battle_&lt;stamp&gt;).</summary>
    public string? WorkRoot { get; init; }

    /// <summary>
    /// Gets the ops to battle (default: all). extract-xiso oracle: list, extract,
    /// rewrite. xdvdfs oracle (xdvdfs-parity features): checksum, md5, unpack,
    /// pack, cso. xboxkit oracle (XboxKit-parity archival): petrify, video,
    /// random, seed, trim, wipe, zar, rebuild.
    /// </summary>
    public List<string> Ops { get; init; } = [.. ValidOps];

    /// <summary>Gets a value indicating whether scratch dirs are kept after the run.</summary>
    public bool KeepWork { get; init; }

    /// <summary>Gets the per-operation timeout in minutes (default 60).</summary>
    public int TimeoutMinutes { get; init; } = 60;

    /// <summary>Gets explicit ISO paths passed positionally; when set, sampling is skipped.</summary>
    public List<string> ExplicitIsos { get; init; } = [];

    /// <summary>Gets a value indicating whether the usage text was requested.</summary>
    public bool Help { get; init; }

    public static BattleOptions Parse(string[] args)
    {
        List<string> dirs = [];
        List<string> explicitIsos = [];
        List<string> ops = [];
        int? seed = null;
        int count = 3;
        int timeout = 60;
        string? cli = null;
        string? oracle = null;
        string? xdvdfs = null;
        string? xboxkit = null;
        string? work = null;
        bool keep = false;
        bool help = args.Length != 0 && args.Any(a => a is "-h" or "--help" or "/?" or "-?");

        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            string? inline = null;
            int eq = a.IndexOf('=');
            if (a.StartsWith("--", StringComparison.Ordinal) && eq > 2)
            {
                inline = a[(eq + 1)..];
                a = a[..eq];
            }

            switch (a)
            {
                case "--dir":
                case "--dirs":
                    string v = Value(args, ref i, inline) ?? string.Empty;
                    dirs.AddRange(v.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
                    break;
                case "--count":
                    count = int.Parse(Value(args, ref i, inline) ?? "3", System.Globalization.CultureInfo.InvariantCulture);
                    break;
                case "--seed":
                    seed = int.Parse(Value(args, ref i, inline) ?? "0", System.Globalization.CultureInfo.InvariantCulture);
                    break;
                case "--cli":
                    cli = Value(args, ref i, inline);
                    break;
                case "--exe":
                case "--native":
                    oracle = Value(args, ref i, inline);
                    break;
                case "--xdvdfs":
                    xdvdfs = Value(args, ref i, inline);
                    break;
                case "--xboxkit":
                    xboxkit = Value(args, ref i, inline);
                    break;
                case "--work":
                    work = Value(args, ref i, inline);
                    break;
                case "--ops":
                    string o = Value(args, ref i, inline) ?? string.Empty;
                    ops.AddRange(o.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
                    break;
                case "--keep":
                    keep = true;
                    break;
                case "--timeout":
                    timeout = int.Parse(Value(args, ref i, inline) ?? "60", System.Globalization.CultureInfo.InvariantCulture);
                    break;
                default:
                    if (a.EndsWith(".iso", StringComparison.OrdinalIgnoreCase))
                    {
                        explicitIsos.Add(a);
                    }
                    else if (!a.StartsWith('-'))
                    {
                        dirs.Add(a);
                    }
                    else if (inline is null && !help)
                    {
                        throw new ArgumentException($"Unknown option: {a}");
                    }

                    break;
            }
        }

        List<string> validOps = ["list", "extract", "rewrite"];
        ops = ops.Select(static o => o.ToLowerInvariant()).Distinct(StringComparer.Ordinal).ToList();
        if (ops.Count == 0)
        {
            ops = [.. ValidOps];
        }
        else if (ops.Except(ValidOps, StringComparer.Ordinal).Any())
        {
            throw new ArgumentException(
                $"Unknown ops: {string.Join(',', ops.Except(ValidOps, StringComparer.Ordinal))} (valid: {string.Join(',', ValidOps)})");
        }

        if (dirs.Count == 0)
        {
            // Default source of candidates: the user's Xbox dump folder.
            if (Directory.Exists(@"H:\XBOXTest"))
            {
                dirs.Add(@"H:\XBOXTest");
            }
        }

        return new BattleOptions
        {
            Dirs = dirs,
            Count = count,
            Seed = seed,
            CliPath = cli,
            OraclePath = oracle,
            XdvdfsPath = xdvdfs,
            XboxkitPath = xboxkit,
            WorkRoot = work,
            Ops = ops,
            KeepWork = keep,
            TimeoutMinutes = timeout,
            ExplicitIsos = explicitIsos,
            Help = help,
        };

        static string Value(string[] a, ref int i, string? inline)
        {
            if (inline is not null)
            {
                return inline;
            }

            if (i + 1 >= a.Length)
            {
                throw new ArgumentException($"Missing value for {a[i]}");
            }

            return a[++i];
        }
    }

    /// <summary>All battleable op names, grouped by oracle.</summary>
    public static readonly string[] ValidOps =
    [
        // extract-xiso oracle (extract-xiso parity)
        "list", "extract", "rewrite",
        // xdvdfs oracle (xdvdfs-parity features)
        "checksum", "md5", "unpack", "pack", "cso",
        // xboxkit oracle (XboxKit-parity archival features)
        "petrify", "video", "random", "seed", "trim", "wipe", "zar", "rebuild",
    ];

    public static void PrintUsage() =>
        Console.WriteLine("""

            Usage: XISOSharp.BattleTests [options] [*.iso ...]

            Battles the XISOSharp CLI against reference tools over a random sample of
            ISOs (default: 3 files from H:\XBOXTest).

            Options:
              --dir <path>[,<path>...]  Dir(s) to scan top-level for *.iso (default: H:\XBOXTest)
              --count <N>               Random sample size (default 3)
              --seed <N>                RNG seed for the sample (default: auto, reported in the report)
              --cli <path>              Path to the XISOSharp CLI exe (default: beside the harness)
              --exe <path>              Path to extract-xiso.exe (default: beside the harness)
              --xdvdfs <path>           Path to xdvdfs.exe (default: beside the harness)
              --xboxkit <path>          Path to xboxkit.exe (default: beside the harness)
              --ops <a,b,c>             Ops to battle (default: all; skipped when the op's oracle is missing)
              --work <dir>              Scratch dir root (default: %TEMP%\xiso_battle_<stamp>)
              --keep                    Keep scratch dirs after the run (they hold ~4x the ISO size)
              --timeout <minutes>       Per-operation timeout (default 60)
              -h, --help                Show this help

            Any positional *.iso path replaces random sampling.

            extract-xiso battles (CLI vs extract-xiso.exe):
              list     -l entry lines must match exactly
              extract  -x -d trees must match: same files (ordinal), same SHA-256 per file, same dirs
              rewrite  -r -d outputs must match byte-for-byte (SHA-256); staged input copies
                       protect the source ISOs from the oracle's in-place rewrite semantics

            xdvdfs battles (CLI vs xdvdfs.exe — xdvdfs-parity features):
              checksum deterministic SHA3-256 image checksums must match exactly
              md5      per-file MD5 lists must agree (CLI ⊆ xdvdfs; extra dir entries noted)
              unpack   --unpack vs `xdvdfs unpack`: extracted trees must match (files+SHA-256+dirs)
              pack     -c (media patch off) vs `xdvdfs pack`: content checksums of both
                       packed images must match (layout-agnostic content parity)
              cso      cso round-trip: XISOSharp compress → xdvdfs cross-reads the CSO
                       (md5 per file) → XISOSharp decompress → checksum vs source

            xboxkit battles (CLI vs xboxkit.exe — XboxKit-parity archival features):
              petrify  --petrify vs `-p`: skeleton images must match byte-for-byte
              video    --video vs `-v`: video partition ISOs must match byte-for-byte
              random   --random vs `-r`: filler data must match byte-for-byte
              seed     --seed vs `-s`: XGD1 PRNG seed (4 bytes) must match
              trim     --trim vs `-t`: trimmed images must match byte-for-byte
              wipe     --wipe vs `-w`: wiped images must match byte-for-byte
              zar      --zar vs `-z`: ZArchive outputs must match byte-for-byte
              rebuild  rebuild a full redump image from components; the rebuilt image
                       must match the original byte-for-byte (both tools)

            Notes:
              Redump-only ops (video/random/seed/trim/wipe/petrify/rebuild) auto-skip on
              trimmed XISOs — the reference tools refuse them there. xboxkit.exe always
              exits 0, so success is detected via output files, not exit codes.
            """);
}
