using System.Buffers.Binary;
using XISOSharp.Models;

namespace XISOSharp;

/// <summary>
/// Salvage rebuild for class-T/S corruption (TODO #26, Phase 2).
/// A bounded walk reusing the <c>-V</c> auditor's hardening limits
/// (<see cref="Constants.MaxTocDepth"/>, per-table entry cap, cycle tracking)
/// copies every entry reachable without tripping a truncation or structural
/// gate into a staging directory, then repacks it through the
/// <see cref="XisoWriter.CreateXiso"/> pipeline into a fresh plain
/// <c>.iso</c>. The source image is only ever read — never written, so no
/// backup is needed and CISO input is allowed (reads go through the
/// decompressed view).
/// Class-C quirks do not block salvage: reserved attribute bits are masked
/// (readers already mask) and path separators in names are sanitized to
/// <c>_</c>, mirroring the in-place repair. Anything that cannot be carried
/// is reported in <see cref="SalvageResult.Skipped"/>: unreadable entries,
/// orphaned right-link tails, and separator-collision (or otherwise
/// host-unusable) names. The rebuilt image is re-audited; a salvage that
/// still fails is reported, not hidden.
/// </summary>
public static class XisoSalvager
{
    private const int CopyBufferSize = 65536;

    /// <summary>
    /// Derives the default salvage output path: the source's directory plus
    /// the source stem with a <c>.salvaged.iso</c> suffix
    /// (<c>game.iso</c>/<c>game.cso</c> → <c>game.salvaged.iso</c>).
    /// </summary>
    /// <param name="sourcePath">Path of the image being salvaged.</param>
    /// <returns>The default output path (the file is not created).</returns>
    /// <exception cref="ArgumentException">The path is null or empty.</exception>
    public static string DefaultOutputPath(string sourcePath)
    {
        ArgumentException.ThrowIfNullOrEmpty(sourcePath);
        var dir = Path.GetDirectoryName(Path.GetFullPath(sourcePath));
        var stem = Path.GetFileNameWithoutExtension(sourcePath);
        return Path.Combine(dir!, stem + ".salvaged.iso");
    }

    /// <summary>
    /// Rebuilds a readable image from a corrupt one: carries every entry the
    /// bounded walk can reach, repacks via <c>CreateXiso</c>, re-audits.
    /// An existing <paramref name="outputPath"/> is overwritten (callers
    /// surface the <c>OverwritePrompt</c> convention); the source is never
    /// modified.
    /// </summary>
    /// <param name="sourcePath">Path of the corrupt image (plain or CISO).</param>
    /// <param name="outputPath">
    /// Destination for the rebuilt plain <c>.iso</c>
    /// (<c>null</c> = <see cref="DefaultOutputPath"/>). A not-yet-existing
    /// parent directory is created.
    /// </param>
    /// <returns>Carried paths, dropped lines, output path, and re-audit issues.</returns>
    /// <exception cref="ArgumentException">A path is null or empty.</exception>
    /// <exception cref="FileNotFoundException">The source file does not exist.</exception>
    /// <exception cref="XisoFormatException">
    /// Not a valid XISO image, or the tree root itself is unreachable
    /// (class R: nothing to salvage with).
    /// </exception>
    /// <exception cref="IOException">Thrown on read/write errors.</exception>
    public static SalvageResult Salvage(string sourcePath, string? outputPath = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(sourcePath);
        if (outputPath != null)
            ArgumentException.ThrowIfNullOrEmpty(outputPath);
        if (!File.Exists(sourcePath))
            throw new FileNotFoundException($"Input file not found: {sourcePath}");

        outputPath ??= DefaultOutputPath(sourcePath);
        var outputDir = Path.GetDirectoryName(Path.GetFullPath(outputPath));
        if (!string.IsNullOrEmpty(outputDir))
            Directory.CreateDirectory(outputDir);

        using var image = XisoReader.OpenImageStream(sourcePath);
        var volInfo = XisoReader.GetVolumeInfo(image, sourcePath);
        if (!volInfo.IsValid)
            throw new XisoFormatException($"Not a valid XISO: {sourcePath}");

        var staging = Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), "xiso_salvage_" + Guid.NewGuid().ToString("N"))).FullName;
        try
        {
            var state = new SalvageState();
            if (volInfo is { RootDirSector: 0, RootDirSize: 0 })
            {
                // Empty volume: the audit passes by definition; repack the
                // empty staging so the output is a valid empty image.
            }
            else
            {
                var fileLength = image.Length;
                var rootDirStart = ((long)volInfo.RootDirSector * Constants.SectorSize) + volInfo.DiscLseek;
                if (rootDirStart < 0 || rootDirStart >= fileLength)
                {
                    throw new XisoFormatException(
                        $"Root directory sector {volInfo.RootDirSector} (offset {rootDirStart}) exceeds file length {fileLength}: no tree root to salvage from {sourcePath} (wrong file or decapitated image).");
                }

                SalvageWalk(image, rootDirStart, rootDirStart, "/", staging, fileLength, volInfo.DiscLseek,
                    new HashSet<long>(), state);
            }

            var isoName = Path.GetFileName(outputPath);
            var rc = XisoWriter.CreateXiso(staging, outputDir, null, null, out _, isoName, null);
            if (rc != 0)
                throw new IOException($"Repacking salvaged files into '{outputPath}' failed.");

            var reaudit = XisoReader.AuditXiso(outputPath);
            return new SalvageResult(state.Copied, state.Skipped, outputPath, reaudit.Issues);
        }
        finally
        {
            try
            {
                if (Directory.Exists(staging))
                    Directory.Delete(staging, true);
            }
            catch
            {
                // Best effort: a leftover staging dir never affects the result.
            }
        }
    }

    private sealed class SalvageState
    {
        public List<string> Copied { get; } = [];
        public List<string> Skipped { get; } = [];
        public Dictionary<string, HashSet<string>> StagedNames { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, HashSet<string>> StagedRawNames { get; } = new(StringComparer.Ordinal);
    }

    /// <summary>
    /// Walks the directory tables exactly like the auditor (same sentinels and
    /// hardening bounds), staging file bytes instead of recording issues. Any
    /// gate the auditor would trip ends that branch with a
    /// <see cref="SalvageState.Skipped"/> line — the closing re-audit of the
    /// rebuilt image is the authority on the output. Unlike the in-place
    /// repairer, pointers from records with reserved attribute bits ARE
    /// followed: reading through them speculatively cannot harm the
    /// read-only source, and every byte copied is bounds-checked first.
    /// </summary>
    private static void SalvageWalk(
        Stream image,
        long dirStart,
        long tableStart,
        string path,
        string stagingDir,
        long fileLength,
        long discLseek,
        HashSet<long> visited,
        SalvageState state,
        // Recursion-depth bound (#16 hardening); kept explicit by design.
        // ReSharper disable once ParameterOnlyUsedForPreconditionCheck.Local
        int depth = 0)
    {
        if (depth > Constants.MaxTocDepth)
        {
            state.Skipped.Add(
                $"'{path}': maximum directory depth {Constants.MaxTocDepth} exceeded (subtree dropped).");
            return;
        }

        var entriesInTable = 0;
        Span<byte> shortBuf = stackalloc byte[2];
        Span<byte> intBuf = stackalloc byte[4];
        Span<byte> byteBuf = stackalloc byte[1];
        Span<byte> headerRest = stackalloc byte[12];

        while (true)
        {
            if (++entriesInTable > Constants.MaxTocEntriesPerTable)
            {
                state.Skipped.Add(
                    $"'{path}': too many entries in one directory table (rest of table dropped).");
                return;
            }

            if (dirStart < 0 || dirStart >= fileLength)
            {
                state.Skipped.Add($"Directory offset {dirStart} ({path}) exceeds file length {fileLength} (dropped).");
                return;
            }

            if (!visited.Add(dirStart))
            {
                state.Skipped.Add(
                    $"Cycle detected: directory entry at offset {dirStart} ({path}) was already visited (branch dropped).");
                return;
            }

            long entryStart;
            ushort lOffset;
            try
            {
                image.Seek(dirStart, SeekOrigin.Begin);
                ReadExact(image, shortBuf);
                lOffset = BinaryPrimitives.ReadUInt16LittleEndian(shortBuf);
                entryStart = dirStart;
            }
            catch (IOException)
            {
                state.Skipped.Add($"'{path}': truncated entry header at offset {dirStart} (dropped).");
                return;
            }

            if (lOffset == Constants.PadShort && dirStart == tableStart)
                return;

            if (lOffset == Constants.EmptyDirectorySentinel && dirStart == tableStart)
            {
                var peekPos = image.Position;
                var isAllZeros = false;
                try
                {
                    ReadExact(image, headerRest);
                    isAllZeros = true;
                    foreach (var b in headerRest)
                    {
                        if (b != 0)
                        {
                            isAllZeros = false;
                            break;
                        }
                    }
                }
                catch (IOException)
                {
                    isAllZeros = false;
                }

                image.Seek(peekPos, SeekOrigin.Begin);
                if (isAllZeros)
                    return;
            }

            if (lOffset != 0 && lOffset != Constants.PadShort)
            {
                var leftSeek = tableStart + ((long)lOffset * Constants.DwordSize);
                if (leftSeek < 0 || leftSeek >= fileLength)
                {
                    state.Skipped.Add(
                        $"Left child offset {lOffset} (seek {leftSeek}) exceeds file length in {path} (subtree dropped).");
                }
                else
                {
                    SalvageWalk(image, leftSeek, tableStart, path, stagingDir, fileLength, discLseek,
                        new HashSet<long>(visited), state, depth + 1);
                }
            }

            uint startSector;
            uint fileSize;
            string filename;
            byte rawAttributes;
            ushort rOffset;
            try
            {
                image.Seek(entryStart + 2, SeekOrigin.Begin);
                ReadExact(image, shortBuf);
                rOffset = BinaryPrimitives.ReadUInt16LittleEndian(shortBuf);

                ReadExact(image, intBuf);
                startSector = BinaryPrimitives.ReadUInt32LittleEndian(intBuf);

                ReadExact(image, intBuf);
                fileSize = BinaryPrimitives.ReadUInt32LittleEndian(intBuf);

                ReadExact(image, byteBuf);
                rawAttributes = byteBuf[0];

                ReadExact(image, byteBuf);
                var filenameLength = byteBuf[0];

                var nameBuf = new byte[filenameLength];
                ReadExact(image, nameBuf);
                filename = Latin1Encoding.Instance.GetString(nameBuf);
            }
            catch (IOException)
            {
                state.Skipped.Add($"'{path}': truncated entry header at offset {entryStart} (dropped).");
                return;
            }

            StageEntry(image, filename, rawAttributes, startSector, fileSize, path, stagingDir,
                fileLength, discLseek, state, depth);

            if (rOffset != 0 && rOffset != Constants.PadShort)
            {
                var rightSeek = tableStart + ((long)rOffset * Constants.DwordSize);
                if (rightSeek < 0 || rightSeek >= fileLength)
                {
                    state.Skipped.Add(
                        $"Right child offset {rOffset} (seek {rightSeek}) exceeds file length in {path} (rest of table dropped).");
                    break;
                }

                dirStart = rightSeek;
                continue;
            }

            break;
        }
    }

    private static void StageEntry(
        Stream image,
        string filename,
        byte rawAttributes,
        uint startSector,
        uint fileSize,
        string path,
        string stagingDir,
        long fileLength,
        long discLseek,
        SalvageState state,
        int depth)
    {
        var stagedName = filename.Replace('/', '_').Replace('\\', '_');
        if (stagedName.Length == 0 || stagedName is "." or "..")
        {
            state.Skipped.Add($"'{path}{filename}': name is not usable on the host filesystem (dropped).");
            return;
        }

        var attributes = Constants.MaskAttributes(rawAttributes);
        var isDir = (attributes & Constants.AttributeDir) != 0;

        if (isDir && depth + 1 > Constants.MaxTocDepth)
        {
            // The descent this directory needs would trip the auditor's depth
            // gate in the rebuilt image, so the entry is dropped rather than
            // carried as a doomed subtree (files need no descent and are
            // unaffected).
            state.Skipped.Add(
                $"'{path}{stagedName}/': maximum directory depth {Constants.MaxTocDepth} exceeded (subtree dropped).");
            return;
        }

        if (!state.StagedNames.TryGetValue(stagingDir, out var siblings))
        {
            siblings = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            state.StagedNames[stagingDir] = siblings;
        }

        if (!state.StagedRawNames.TryGetValue(stagingDir, out var rawSiblings))
        {
            rawSiblings = new HashSet<string>(StringComparer.Ordinal);
            state.StagedRawNames[stagingDir] = rawSiblings;
        }

        if (!siblings.Add(stagedName))
        {
            // Either side of the collision may carry the separators (walk
            // order decides who stages first), so a sanitized rival counts as
            // a separator collision too — only byte-identical rivals are
            // plain duplicates.
            var sibSanitized = rawSiblings.Any(r =>
                !string.Equals(r, filename, StringComparison.Ordinal) &&
                r.Replace('/', '_').Replace('\\', '_').Equals(stagedName, StringComparison.OrdinalIgnoreCase));
            var why = filename.Contains('/') || filename.Contains('\\') || sibSanitized
                ? "separator-collision"
                : "duplicate";
            state.Skipped.Add($"'{path}{filename}': {why} name '{stagedName}' already staged (dropped).");
            return;
        }

        rawSiblings.Add(filename);
        var stagedPath = Path.Combine(stagingDir, stagedName);

        if (isDir)
        {
            try
            {
                Directory.CreateDirectory(stagedPath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                siblings.Remove(stagedName);
                rawSiblings.Remove(filename);
                state.Skipped.Add($"'{path}{filename}': cannot stage directory ({ex.Message}) (dropped).");
                return;
            }

            state.Copied.Add($"{path}{stagedName}/");
            var sectorOffset = ((long)startSector * Constants.SectorSize) + discLseek;
            if (fileSize > 0)
            {
                if (sectorOffset < 0 || sectorOffset >= fileLength)
                {
                    state.Skipped.Add(
                        $"Sector {startSector} (offset {sectorOffset}) for '{path}{filename}' exceeds file length {fileLength} (contents dropped).");
                    return;
                }

                SalvageWalk(image, sectorOffset, sectorOffset, $"{path}{stagedName}/", stagedPath,
                    fileLength, discLseek, new HashSet<long>(), state, depth + 1);
            }

            return;
        }

        var dataOffset = ((long)startSector * Constants.SectorSize) + discLseek;
        if (dataOffset < 0 || dataOffset >= fileLength)
        {
            siblings.Remove(stagedName);
            rawSiblings.Remove(filename);
            state.Skipped.Add(
                $"Sector {startSector} (offset {dataOffset}) for '{path}{filename}' exceeds file length {fileLength} (dropped).");
            return;
        }

        long endOffset = dataOffset + fileSize;
        if (endOffset > fileLength)
        {
            siblings.Remove(stagedName);
            rawSiblings.Remove(filename);
            state.Skipped.Add(
                $"'{path}{filename}' size {fileSize} (ends at {endOffset}) exceeds file length {fileLength} (dropped).");
            return;
        }

        try
        {
            image.Seek(dataOffset, SeekOrigin.Begin);
            using var outFile = new FileStream(stagedPath, FileMode.Create, FileAccess.Write, FileShare.None,
                CopyBufferSize);
            var remaining = (long)fileSize;
            var buffer = new byte[CopyBufferSize];
            while (remaining > 0)
            {
                var want = (int)Math.Min(buffer.Length, remaining);
                ReadExact(image, buffer.AsSpan(0, want));
                outFile.Write(buffer, 0, want);
                remaining -= want;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            siblings.Remove(stagedName);
            rawSiblings.Remove(filename);
            try
            {
                if (File.Exists(stagedPath))
                    File.Delete(stagedPath);
            }
            catch
            {
                // Best effort: the staging dir is removed wholesale afterwards.
            }

            state.Skipped.Add($"'{path}{filename}': cannot stage file data ({ex.Message}) (dropped).");
            return;
        }

        state.Copied.Add($"{path}{stagedName}");
    }

    private static void ReadExact(Stream stream, Span<byte> buffer)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = stream.Read(buffer[offset..]);
            if (read <= 0)
                throw new IOException($"Read error: expected {buffer.Length} bytes, got {offset}");

            offset += read;
        }
    }
}