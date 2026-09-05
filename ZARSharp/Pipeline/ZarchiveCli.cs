namespace ZARSharp.Pipeline;

/// <summary>
/// Callable form of the <c>zarchive.exe input_path [output_path]</c> contract
/// (<c>src/main.cpp</c>, ZArchive 0.1.2): a directory input packs, a file
/// input extracts, outputs default to <c>&lt;stem&gt;.zar</c> /
/// <c>&lt;stem&gt;_extracted</c> next to the input, existing pack outputs are
/// refused, and incomplete pack outputs are deleted. Process exit codes are
/// the same negative values <c>main()</c> returns, so automation matching on
/// them keeps working.
/// </summary>
public static class ZarchiveCli
{
    /// <summary>Success.</summary>
    public const int Ok = 0;

    /// <summary>Usage error: too many paths, or the input is neither file nor directory.</summary>
    public const int BadUsage = -1;

    /// <summary>Extract output path exists and is not a directory.</summary>
    public const int OutputNotDirectory = -3;

    /// <summary>Extract output directory could not be created.</summary>
    public const int OutputDirectoryNotCreated = -4;

    /// <summary>Archive file not found, or pack output exists and is not a regular file.</summary>
    public const int NotFound = -10;

    /// <summary>Archive failed to open, or pack output already exists.</summary>
    public const int Refused = -11;

    /// <summary>Extraction failed (corrupt archive or I/O).</summary>
    public const int ExtractionFailed = -12;

    /// <summary>Pack failed on archive structure.</summary>
    public const int PackFailed = -13;

    /// <summary>Pack failed on output I/O.</summary>
    public const int PackOutputFailed = -16;

    /// <summary>
    /// Runs <c>zarchive.exe</c> argument parsing over <paramref name="args"/>
    /// (0..2 paths). No arguments prints the usage text and returns
    /// <see cref="Ok"/>, like <c>main()</c>.
    /// </summary>
    public static int Run(
        string[] args,
        ZarPipelineOptions? options = null,
        IProgress<ZarProgress>? progress = null,
        Action<string>? log = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(args);
        if (args.Length == 0)
        {
            log?.Invoke("Usage:\n");
            log?.Invoke("zarchive.exe input_path [output_path]");
            log?.Invoke("If input_path is a directory, then output_path will be the ZArchive output file path");
            log?.Invoke("If input_path is a ZArchive file path, then output_path will be the output directory");
            log?.Invoke("output_path is optional");
            return Ok;
        }

        if (args.Length > 2)
        {
            log?.Invoke("Too many paths specified");
            return BadUsage;
        }

        return Run(args[0], args.Length > 1 ? args[1] : null, options, progress, log, cancellationToken);
    }

    /// <summary>Runs one pack-or-extract operation, dispatching on input kind like <c>main()</c>.</summary>
    public static int Run(
        string inputPath,
        string? outputPath = null,
        ZarPipelineOptions? options = null,
        IProgress<ZarProgress>? progress = null,
        Action<string>? log = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(inputPath);
        options ??= new ZarPipelineOptions();
        try
        {
            if (File.Exists(inputPath))
            {
                return ExtractFile(inputPath, outputPath, options, progress, log, cancellationToken);
            }

            if (Directory.Exists(inputPath))
            {
                return PackDirectory(inputPath, outputPath, options, progress, log, cancellationToken);
            }

            log?.Invoke("Input path is not a valid file or directory");
            return BadUsage;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            log?.Invoke(ex.Message);
            return PackFailed;
        }
    }

    private static int ExtractFile(
        string inputPath, string? outputPath, ZarPipelineOptions options,
        IProgress<ZarProgress>? progress, Action<string>? log, CancellationToken cancellationToken)
    {
        string outputDirectory;
        if (outputPath == null)
        {
            var dir = Path.GetDirectoryName(Path.GetFullPath(inputPath)) ?? "";
            outputDirectory = Path.Combine(dir, Path.GetFileNameWithoutExtension(inputPath) + "_extracted");
            log?.Invoke($"Extracting to: {outputDirectory}");
        }
        else
        {
            outputDirectory = outputPath;
        }

        if ((File.Exists(outputDirectory) || Directory.Exists(outputDirectory)) &&
            !Directory.Exists(outputDirectory))
        {
            log?.Invoke("The specified output path is not a valid directory");
            return OutputNotDirectory;
        }

        try
        {
            Directory.CreateDirectory(outputDirectory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log?.Invoke("Failed to create output directory");
            return OutputDirectoryNotCreated;
        }

        if (!Directory.Exists(outputDirectory))
        {
            log?.Invoke("Failed to create output directory");
            return OutputDirectoryNotCreated;
        }

        try
        {
            var files = ZarPipeline.Extract(inputPath, outputDirectory, options, progress, cancellationToken);
            foreach (var file in files)
            {
                log?.Invoke(file);
            }

            return Ok;
        }
        catch (FileNotFoundException)
        {
            log?.Invoke("Unable to find archive file");
            return NotFound;
        }
        catch (InvalidOperationException)
        {
            log?.Invoke("Extraction failed");
            return ExtractionFailed;
        }
    }

    private static int PackDirectory(
        string inputPath, string? outputPath, ZarPipelineOptions options,
        IProgress<ZarProgress>? progress, Action<string>? log, CancellationToken cancellationToken)
    {
        string outputFile;
        if (outputPath == null)
        {
            outputFile = ZarPipeline.DefaultZarPath(inputPath);
            log?.Invoke($"Outputting to: {outputFile}");
        }
        else
        {
            outputFile = outputPath;
        }

        if ((File.Exists(outputFile) || Directory.Exists(outputFile)) && !File.Exists(outputFile))
        {
            log?.Invoke("The specified output path is not a valid file");
            return NotFound;
        }

        if (File.Exists(outputFile))
        {
            log?.Invoke("The output file already exists");
            return Refused;
        }

        var fileProgress = log == null ? progress : new LoggingProgress(progress, log);
        try
        {
            // Refuse-overwrite without exceptions: the checks above already
            // cover it, so force Fail and map the outcome to exit codes.
            var packOptions = ShallowCopy(options);
            packOptions.CollisionPolicy = ZarCollisionPolicy.Fail;
            ZarPipeline.Pack(inputPath, outputFile, packOptions, fileProgress, cancellationToken);
            return Ok;
        }
        catch (DirectoryNotFoundException)
        {
            log?.Invoke("Input path is not a valid file or directory");
            return BadUsage;
        }
        catch (IOException ex)
        {
            log?.Invoke(ex.Message);
            return PackOutputFailed;
        }
        catch (InvalidOperationException ex)
        {
            log?.Invoke(ex.Message);
            return PackFailed;
        }
    }

    private static ZarPipelineOptions ShallowCopy(ZarPipelineOptions options) => new()
    {
        Level = options.Level,
        Checksum = options.Checksum,
        Compressor = options.Compressor,
        DeterministicOrder = options.DeterministicOrder,
        CollisionPolicy = options.CollisionPolicy,
        MaxDegreeOfParallelism = options.MaxDegreeOfParallelism,
        DeleteSourceOnSuccess = options.DeleteSourceOnSuccess,
        Pause = options.Pause,
    };

    /// <summary>Forwards per-file progress as <c>Adding &lt;path&gt;</c> log lines (the <c>main.cpp</c> pack chatter).</summary>
    private sealed class LoggingProgress(IProgress<ZarProgress>? inner, Action<string> log) : IProgress<ZarProgress>
    {
        private string _last = string.Empty;

        public void Report(ZarProgress value)
        {
            inner?.Report(value);
            if (value.CurrentFile.Length != 0 && !string.Equals(value.CurrentFile, _last, StringComparison.Ordinal))
            {
                _last = value.CurrentFile;
                log($"Adding {value.CurrentFile}");
            }
        }
    }
}
