using System.Globalization;

namespace XISOSharp.Cli;

/// <summary>
/// Parsed <c>--batch</c> flags shared by the container verbs
/// (<c>compress</c>, <c>decompress</c>, <c>convert</c> and <c>split</c>).
/// </summary>
internal sealed class BatchOptions
{
    /// <summary>Directory scanned for sources; <c>null</c> means single-source mode.</summary>
    internal string? Directory { get; set; }

    /// <summary>Recurse into subdirectories.</summary>
    internal bool Recursive { get; set; }

    /// <summary>Skip a source when all of its expected outputs already exist.</summary>
    internal bool SkipExisting { get; set; }

    /// <summary>Keep converting after a per-file failure and report the failures at the end.</summary>
    internal bool ContinueOnError { get; set; }

    /// <summary>
    /// Delete each source image after its conversion succeeds. Requires
    /// <c>-y</c>/<c>--yes</c>; a split set deletes every part.
    /// </summary>
    internal bool DeleteSource { get; set; }

    /// <summary>Maximum conversions run in parallel (default 1).</summary>
    internal int Jobs { get; set; } = 1;

    /// <summary>True when <c>--jobs</c> appeared (used to reject it outside batch mode).</summary>
    internal bool JobsSpecified { get; set; }

    /// <summary>Output-name template; <c>null</c> uses the per-format default.</summary>
    internal string? OutputName { get; set; }

    /// <summary>Overwrite existing outputs without prompting.</summary>
    internal bool AssumeYes { get; set; }

    /// <summary>Never overwrite existing outputs.</summary>
    internal bool AssumeNo { get; set; }
}

/// <summary>
/// Shared batch conversion support for the standalone container verbs:
/// source discovery, output-name templates, collision guards, per-file
/// overwrite policy, optional parallelism and the closing summary.
/// </summary>
internal static class BatchConversion
{
    /// <summary>Maximum accepted <c>--jobs</c> value.</summary>
    internal const int MaxJobs = 64;

    /// <summary>
    /// Consumes one batch-related argument. Returns <c>true</c> when
    /// <paramref name="args"/>[<paramref name="i"/>] was a batch flag (and
    /// advances <paramref name="i"/> past its value); <paramref name="error"/>
    /// is set when the flag's value is missing or invalid.
    /// </summary>
    internal static bool TryConsume(string[] args, ref int i, BatchOptions options, out string? error)
    {
        error = null;
        string a = args[i];

        if (string.Equals(a, "--batch", StringComparison.OrdinalIgnoreCase))
        {
            if (i + 1 >= args.Length)
            {
                error = "--batch requires a directory";
                return true;
            }

            options.Directory = args[++i];
            return true;
        }

        if (string.Equals(a, "--batch-recursive", StringComparison.OrdinalIgnoreCase))
        {
            options.Recursive = true;
            return true;
        }

        if (string.Equals(a, "--skip-existing", StringComparison.OrdinalIgnoreCase))
        {
            options.SkipExisting = true;
            return true;
        }

        if (string.Equals(a, "--continue-on-error", StringComparison.OrdinalIgnoreCase))
        {
            options.ContinueOnError = true;
            return true;
        }

        if (string.Equals(a, "--delete-source", StringComparison.OrdinalIgnoreCase))
        {
            options.DeleteSource = true;
            return true;
        }

        if (string.Equals(a, "--jobs", StringComparison.OrdinalIgnoreCase))
        {
            if (i + 1 >= args.Length ||
                !int.TryParse(args[i + 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int jobs) ||
                jobs < 1 || jobs > MaxJobs)
            {
                error = $"--jobs requires an integer 1..{MaxJobs}";
                return true;
            }

            options.Jobs = jobs;
            options.JobsSpecified = true;
            i++;
            return true;
        }

        if (string.Equals(a, "--output-name", StringComparison.OrdinalIgnoreCase))
        {
            if (i + 1 >= args.Length)
            {
                error = "--output-name requires a template";
                return true;
            }

            options.OutputName = args[++i];
            return true;
        }

        return false;
    }

    /// <summary>
    /// Validates batch-only flags after parsing. Returns the error line, or
    /// <c>null</c> when the combination is valid.
    /// </summary>
    internal static string? Validate(BatchOptions options)
    {
        bool batch = options.Directory != null;

        if (!batch)
        {
            if (options.Recursive)
                return "--batch-recursive requires --batch <directory>";
            if (options.SkipExisting)
                return "--skip-existing requires --batch <directory>";
            if (options.ContinueOnError)
                return "--continue-on-error requires --batch <directory>";
            if (options.DeleteSource)
                return "--delete-source requires --batch <directory>";
            if (options.JobsSpecified)
                return "--jobs requires --batch <directory>";
            if (options.OutputName != null)
                return "--output-name requires --batch <directory>";
            return null;
        }

        if (options.OutputName != null)
        {
            string? templateError = ValidateTemplate(options.OutputName);
            if (templateError != null)
                return templateError;
        }

        return null;
    }

    /// <summary>
    /// Returns the first unknown <c>{token}</c> in an output-name template, or
    /// <c>null</c> when every token is one of <c>{name}</c>, <c>{ext}</c> or
    /// <c>{format}</c>.
    /// </summary>
    internal static string? ValidateTemplate(string template)
    {
        if (string.IsNullOrWhiteSpace(template))
            return "--output-name requires a non-empty template";

        for (int i = 0; i < template.Length; i++)
        {
            if (template[i] != '{')
                continue;

            int end = template.IndexOf('}', i + 1);
            if (end < 0)
                return "--output-name has an unterminated '{'";

            string token = template[(i + 1)..end];
            if (!token.Equals("name", StringComparison.OrdinalIgnoreCase) &&
                !token.Equals("ext", StringComparison.OrdinalIgnoreCase) &&
                !token.Equals("format", StringComparison.OrdinalIgnoreCase))
            {
                return $"--output-name has unknown placeholder '{{{token}}}' " +
                       "(supported: {name}, {ext}, {format})";
            }

            i = end;
        }

        return null;
    }

    /// <summary>
    /// Expands <c>{name}</c> (source stem with a <c>.1</c> split marker and
    /// container extension stripped), <c>{ext}</c> (source extension without
    /// the dot) and <c>{format}</c> (output extension).
    /// </summary>
    internal static string ExpandTemplate(string template, string sourcePath, string format)
    {
        string extension = Path.GetExtension(sourcePath).TrimStart('.');
        return template
            .Replace("{name}", BaseName(sourcePath), StringComparison.OrdinalIgnoreCase)
            .Replace("{ext}", extension, StringComparison.OrdinalIgnoreCase)
            .Replace("{format}", format, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Resolves the output path for one batch source. Relative templates are
    /// resolved against the source directory; an output without an extension
    /// gets <c>.<paramref name="format"/></c> when
    /// <paramref name="appendExtension"/> is set.
    /// </summary>
    internal static string OutputPath(string sourcePath, string? template, string format, bool appendExtension)
    {
        string directory = Path.GetDirectoryName(sourcePath) ?? string.Empty;
        string name;
        if (template is null)
        {
            name = BaseName(sourcePath);
            if (appendExtension)
                name += "." + format;
        }
        else
        {
            name = ExpandTemplate(template, sourcePath, format);
            if (appendExtension && string.IsNullOrEmpty(Path.GetExtension(name)))
                name += "." + format;
        }

        string combined = Path.IsPathRooted(name) ? name : Path.Combine(directory, name);
        return Path.GetFullPath(combined);
    }

    /// <summary>
    /// Source stem with the image extension removed and, for split-capable
    /// ISO/CISO/CCI names, a trailing <c>.1</c> split marker: <c>game.iso</c> 
    /// <c>game</c>, <c>game.1.cso</c>  <c>game</c>, <c>game.1.cci</c> 
    /// <c>game</c>. A numbered name in another format (<c>archive.1.zar</c>)
    /// keeps its number.
    /// </summary>
    internal static string BaseName(string path)
    {
        string file = Path.GetFileName(path);
        string extension = Path.GetExtension(file);
        string stem = extension.Length > 0 ? file[..^extension.Length] : file;
        bool splitCapable = extension.Equals(".iso", StringComparison.OrdinalIgnoreCase) ||
                            extension.Equals(".cso", StringComparison.OrdinalIgnoreCase) ||
                            extension.Equals(".cci", StringComparison.OrdinalIgnoreCase);
        if (splitCapable && stem.EndsWith(".1", StringComparison.Ordinal))
            stem = stem[..^2];
        return stem;
    }

    /// <summary>
    /// Enumerates sources matching <paramref name="extensions"/> (case-insensitive
    /// on every platform), sorted for deterministic runs. When
    /// <paramref name="includeFirstPart"/> is set, the first part of a split
    /// set (<c>game.1.iso</c>) is kept and later parts are dropped; otherwise
    /// every numbered part is dropped.
    /// </summary>
    internal static List<string> EnumerateSources(
        string directory,
        bool recursive,
        bool includeFirstPart,
        params string[] extensions)
    {
        if (!Directory.Exists(directory))
            throw new DirectoryNotFoundException($"batch directory not found: {directory}");

        EnumerationOptions enumeration = new()
        {
            MatchCasing = MatchCasing.CaseInsensitive,
            RecurseSubdirectories = recursive,
            AttributesToSkip = FileAttributes.None
        };

        List<string> sources = [];
        foreach (string path in Directory.EnumerateFiles(directory, "*", enumeration))
        {
            string extension = Path.GetExtension(path);
            if (!extensions.Any(e => extension.Equals(e, StringComparison.OrdinalIgnoreCase)))
                continue;

            int part = GetSplitPart(path);
            if (part > 0 && (!includeFirstPart || part != 1))
                continue;

            sources.Add(Path.GetFullPath(path));
        }

        sources.Sort(StringComparer.OrdinalIgnoreCase);
        return sources;
    }

    /// <summary>
    /// Returns the numeric split-part index of an ISO/CISO/CCI image name
    /// (<c>game.2.iso</c>  2), or 0 when the name is not a numbered part.
    /// Only small indexes (1..999) count, so names such as
    /// <c>game.2004.iso</c> stay ordinary images; other extensions (notably
    /// <c>.zar</c>) are never treated as split parts.
    /// </summary>
    internal static int GetSplitPart(string path)
    {
        string extension = Path.GetExtension(path);
        if (!extension.Equals(".iso", StringComparison.OrdinalIgnoreCase) &&
            !extension.Equals(".cso", StringComparison.OrdinalIgnoreCase) &&
            !extension.Equals(".cci", StringComparison.OrdinalIgnoreCase))
        {
            // Only ISO/CISO/CCI sets are numbered parts; `archive.1.zar` is a
            // perfectly ordinary ZArchive name.
            return 0;
        }

        string stem = Path.GetFileNameWithoutExtension(path);
        int dot = stem.LastIndexOf('.');
        if (dot <= 0 || dot == stem.Length - 1)
            return 0;

        if (!int.TryParse(stem.AsSpan(dot + 1), NumberStyles.None, CultureInfo.InvariantCulture, out int part) ||
            part is < 1 or > 999)
        {
            return 0;
        }

        return part;
    }

    /// <summary>
    /// Returns every file that belongs to a batch source. For a split set
    /// (<c>game.1.iso</c>, <c>game.1.cso</c>, <c>game.1.cci</c>) this is every
    /// contiguous part starting at 1; otherwise it is just the source file.
    /// Used by <c>--delete-source</c> so a completed conversion removes the
    /// whole set, not only its first part.
    /// </summary>
    internal static IReadOnlyList<string> SourceFiles(string source)
    {
        string full = Path.GetFullPath(source);
        string extension = Path.GetExtension(full);
        if (GetSplitPart(full) <= 0)
            return [full];

        string stem = Path.GetFileNameWithoutExtension(full);
        int dot = stem.LastIndexOf('.');
        if (dot <= 0)
            return [full];

        string baseName = stem[..dot];
        string directory = Path.GetDirectoryName(full) ?? string.Empty;
        List<string> files = [];
        for (int part = 1; part <= 999; part++)
        {
            string candidate = Path.Combine(
                directory,
                string.Concat(baseName, ".", part.ToString(CultureInfo.InvariantCulture), extension));
            if (!File.Exists(candidate))
                break;

            files.Add(candidate);
        }

        return files.Count > 0 ? files : [full];
    }

    /// <summary>
    /// Computes the part paths a split of <paramref name="isoPath"/> would
    /// write. Returns an empty list when the source cannot be probed; the
    /// split call itself then reports the real error.
    /// </summary>
    internal static IReadOnlyList<string> SplitTargets(string isoPath, string splitBase, long partSize, bool halves)
    {
        try
        {
            if (!File.Exists(isoPath))
                return [];

            long length = new FileInfo(isoPath).Length;
            if (length <= 0)
                return [];

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
                    return [];
            }

            int count = (int)Math.Min(int.MaxValue, ((length - 1) / chunk) + 1);
            List<string> parts = new(count);
            for (int i = 0; i < count; i++)
                parts.Add(XisoSplitter.PartPath(splitBase, i));
            return parts;
        }
        catch
        {
            // Probing must never fail the command; Split reports real errors.
            return [];
        }
    }

    /// <summary>
    /// Runs a batch conversion plan: resolves every output (rejecting
    /// duplicate outputs and outputs that would clobber an input), applies
    /// <c>--skip-existing</c> and the overwrite prompt, then converts the
    /// remaining sources sequentially or with <c>--jobs</c> workers.
    /// </summary>
    /// <param name="verb">Verb name used in status and summary lines.</param>
    /// <param name="options">Parsed batch flags.</param>
    /// <param name="sources">Full source paths, in deterministic order.</param>
    /// <param name="outputFor">Maps a source to its primary output path.</param>
    /// <param name="targetsFor">Maps a source and its primary output to every file it writes (split parts included).</param>
    /// <param name="worker">Performs one conversion; returns 0 on success.</param>
    /// <param name="guard">Optional input/output guard returning an error line to refuse the plan.</param>
    internal static int Run(
        string verb,
        BatchOptions options,
        IReadOnlyList<string> sources,
        Func<string, string> outputFor,
        Func<string, string, IReadOnlyList<string>> targetsFor,
        Func<string, string, int> worker,
        Func<string, string, string?>? guard = null)
    {
        if (options.DeleteSource && (options.AssumeNo || !options.AssumeYes))
        {
            Logger.LogErr("Error: --delete-source requires -y/--yes and cannot be combined with -n/--no\n");
            return 1;
        }

        Logger.Log($"batch {verb}: {sources.Count} file(s) from {options.Directory}" +
                   $"{(options.Recursive ? " (recursive)" : "")}\n");

        HashSet<string> sourceSet = new(sources, StringComparer.OrdinalIgnoreCase);
        HashSet<string> seenOutputs = new(StringComparer.OrdinalIgnoreCase);
        HashSet<string> targetSet = new(StringComparer.OrdinalIgnoreCase);
        List<BatchItem> plan = new(sources.Count);

        foreach (string source in sources)
        {
            string output = outputFor(source);

            string? refusal = guard?.Invoke(source, output);
            if (refusal != null)
            {
                Logger.LogErr(refusal);
                return 1;
            }

            if (sourceSet.Contains(output))
            {
                Logger.LogErr($"Error: {verb} output {output} is also a batch input; choose another name\n");
                return 1;
            }

            if (!seenOutputs.Add(output))
            {
                Logger.LogErr($"Error: {verb} outputs collide on {output}; " +
                              "use --output-name to keep them distinct\n");
                return 1;
            }

            foreach (string target in targetsFor(source, output))
            {
                if (sourceSet.Contains(Path.GetFullPath(target)))
                {
                    Logger.LogErr($"Error: {verb} output part {target} is also a batch input; " +
                                  "choose another name\n");
                    return 1;
                }

                targetSet.Add(Path.GetFullPath(target));
            }

            plan.Add(new BatchItem(source, output));
        }

        if (options.DeleteSource)
        {
            // Never delete an input that another item writes. The planning
            // checks above already reject such plans; this is the final guard.
            foreach (string source in sources)
            {
                foreach (string sourceFile in SourceFiles(source))
                {
                    if (targetSet.Contains(sourceFile))
                    {
                        Logger.LogErr($"Error: {verb} cannot delete source {sourceFile}; " +
                                      "it is also a batch output\n");
                        return 1;
                    }
                }
            }
        }

        int converted = 0;
        int skipped = 0;
        int failed = 0;
        int deleted = 0;
        List<BatchItem> ready = new(plan.Count);

        foreach (BatchItem item in plan)
        {
            IReadOnlyList<string> targets = targetsFor(item.Source, item.Output);
            List<string> existing = targets
                .Where(t => File.Exists(t) || Directory.Exists(t))
                .ToList();
            bool complete = targets.Count > 0 && existing.Count == targets.Count;

            // --skip-existing means "skip a finished conversion". For a split
            // output, every expected part must exist; a partial set is resumed
            // (and overwritten) instead of being mistaken for a complete one.
            if (complete && options.SkipExisting)
            {
                skipped++;
                Logger.Log($"{verb}: {Path.GetFileName(item.Source)} -> " +
                           $"{Path.GetFileName(item.Output)} [SKIP] output exists\n");
                continue;
            }

            if (existing.Count > 0 && !options.SkipExisting)
            {
                if (options.AssumeNo)
                {
                    skipped++;
                    Logger.Log($"{verb}: {Path.GetFileName(item.Source)} -> " +
                               $"{Path.GetFileName(item.Output)} [SKIP] output exists\n");
                    continue;
                }

                bool confirmed = true;
                foreach (string target in existing)
                {
                    if (!OverwritePrompt.ConfirmOverwrite(target, options.AssumeYes, options.AssumeNo))
                    {
                        confirmed = false;
                        break;
                    }
                }

                if (!confirmed)
                {
                    failed++;
                    Logger.LogErr($"{verb}: {Path.GetFileName(item.Source)} [FAIL] output exists\n");
                    if (!options.ContinueOnError)
                        break;
                    continue;
                }
            }

            ready.Add(item);
        }

        if (ready.Count > 0 && options.Jobs > 1 && ready.Count > 1)
        {
            int parallelConverted = 0;
            int parallelFailed = 0;
            using CancellationTokenSource cancellation = new();
            try
            {
                Parallel.ForEach(ready,
                    new ParallelOptions { MaxDegreeOfParallelism = options.Jobs, CancellationToken = cancellation.Token },
                    item =>
                    {
                        if (Execute(item) == 0)
                        {
                            Interlocked.Increment(ref parallelConverted);
                        }
                        else
                        {
                            Interlocked.Increment(ref parallelFailed);
                            if (!options.ContinueOnError)
                                // Parallel.ForEach returns only after every iteration
                                // finishes, so the CTS is still alive here.
                                // ReSharper disable once AccessToDisposedClosure
                                cancellation.Cancel();
                        }
                    });
            }
            catch (OperationCanceledException)
            {
                // First failure without --continue-on-error stopped the run.
            }

            converted += parallelConverted;
            failed += parallelFailed;
        }
        else
        {
            foreach (BatchItem item in ready)
            {
                if (Execute(item) == 0)
                {
                    converted++;
                }
                else
                {
                    failed++;
                    if (!options.ContinueOnError)
                        break;
                }
            }
        }

        string summary = $"batch {verb}: {converted} converted, {skipped} skipped, {failed} failed";
        if (options.DeleteSource)
            summary += $", {deleted} deleted";
        Logger.Log(summary + "\n");
        return failed == 0 ? 0 : 1;

        int Execute(BatchItem item)
        {
            try
            {
                string? outputDirectory = Path.GetDirectoryName(item.Output);
                if (!string.IsNullOrEmpty(outputDirectory))
                    Directory.CreateDirectory(outputDirectory);

                int rc = worker(item.Source, item.Output);
                if (rc != 0)
                {
                    Logger.LogErr($"{verb}: {Path.GetFileName(item.Source)} [FAIL] exit code {rc}\n");
                    DeleteFailedOutputs(item);
                    return 1;
                }

                if (options.DeleteSource && !TryDeleteSource(item.Source, out string? deleteError))
                {
                    Logger.LogErr($"{verb}: {Path.GetFileName(item.Source)} [FAIL] converted but " +
                                  $"could not delete source: {deleteError}\n");
                    return 1;
                }

                Logger.Log($"{verb}: {Path.GetFileName(item.Source)} -> " +
                           $"{Path.GetFileName(item.Output)} [OK]\n");
                return 0;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Logger.LogErr($"{verb}: {Path.GetFileName(item.Source)} [FAIL] {ex.Message}\n");
                DeleteFailedOutputs(item);
                return 1;
            }
        }

        bool TryDeleteSource(string source, out string? error)
        {
            error = null;
            try
            {
                IReadOnlyList<string> files = SourceFiles(source);
                foreach (string file in files)
                {
                    if (targetSet.Contains(file))
                    {
                        error = $"{file} is also a batch output";
                        return false;
                    }
                }

                foreach (string file in files)
                {
                    if (File.Exists(file))
                        File.Delete(file);
                }

                Interlocked.Increment(ref deleted);
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
                                           ArgumentException or NotSupportedException)
            {
                error = ex.Message;
                return false;
            }
        }

        void DeleteFailedOutputs(BatchItem item)
        {
            // A failed conversion must not leave a truncated output behind
            // (CisoReader creates the destination before it validates the
            // header). The overwrite prompt already authorized replacing any
            // pre-existing target, so removing the partial file is safe.
            try
            {
                foreach (string target in targetsFor(item.Source, item.Output))
                {
                    if (File.Exists(target))
                        File.Delete(target);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Logger.LogErr($"{verb}: could not remove partial output for " +
                              $"{Path.GetFileName(item.Source)}: {ex.Message}\n");
            }
        }
    }

    /// <summary>One source/output pair in a batch plan.</summary>
    private readonly record struct BatchItem(string Source, string Output);
}
