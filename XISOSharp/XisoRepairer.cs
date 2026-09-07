using System.Buffers.Binary;
using System.Text;
using XISOSharp.Models;

namespace XISOSharp;

/// <summary>
/// In-place repair of class-C volume issues (TODO #26, Phase 1).
/// Fixes exactly what the <c>-V</c> auditor flags as safely patchable, byte for
/// byte, without changing the image size or touching any other bytes:
/// <list type="bullet">
/// <item>Reserved attribute bits — the entry's attribute byte is rewritten with
/// <see cref="Constants.MaskAttributes"/> (readers already mask, so this is pure
/// normalization).</item>
/// <item>Missing optimized tag — the exact tag bytes <see cref="XisoWriter"/>
/// writes are stored at <see cref="Constants.OptimizedTagOffset"/>.</item>
/// <item>Path separators in filenames — length-preserving <c>_</c> substitution
/// (the name length byte is untouched, so the table layout cannot shift). When
/// two names would collide after substitution, the entry is left alone and the
/// re-audit reports it.</item>
/// </list>
/// Truncation, structural (depth/chain/cycle), and refused (bad magic/root)
/// classes are never repaired in place — inventing missing bytes or performing
/// pointer surgery risks destroying readable data (Phase 2 salvage rebuild).
/// Every pass ends with a re-audit, so verification and idempotency come free:
/// repairing a clean image is a no-op that reports success.
/// </summary>
public static class XisoRepairer
{
    /// <summary>
    /// Repair passes are bounded; real images settle in at most two (fixing a
    /// directory's attribute byte unlocks exactly one more level down).
    /// </summary>
    private const int MaxRepairPasses = 5;

    /// <summary>
    /// Repairs the class-C issues of an XISO image in place.
    /// A <c>.old</c> backup of the pre-repair image is written first (replacing
    /// any previous backup) unless disabled — the copy-in precedent (#22).
    /// </summary>
    /// <param name="isoPath">Path to the XISO image (modified in place).</param>
    /// <param name="createBackup">Write a <c>.old</c> backup first (default true).</param>
    /// <param name="dryRun">Preview the fixes without changing anything (default false).</param>
    /// <returns>
    /// The applied (or would-be) fixes plus the post-repair audit issues.
    /// <see cref="RepairResult.Success"/> is true when the image now passes.
    /// </returns>
    /// <exception cref="ArgumentException">The path is null or empty.</exception>
    /// <exception cref="FileNotFoundException">The image file does not exist.</exception>
    /// <exception cref="XisoFormatException">The image is not a valid XISO.</exception>
    /// <exception cref="InvalidDataException">
    /// The image is a CISO container (decompress first) or a split part
    /// (reassemble first); neither is patch-stable in place.
    /// </exception>
    /// <exception cref="IOException">Thrown on read/write errors.</exception>
    public static RepairResult RepairInPlace(string isoPath, bool createBackup = true, bool dryRun = false)
    {
        ArgumentException.ThrowIfNullOrEmpty(isoPath);
        if (XisoReader.IsCsoPath(isoPath) || CisoReader.IsCso(isoPath))
        {
            throw new InvalidDataException(
                $"Cannot repair CISO container '{isoPath}' in place (compressed bytes are not patch-stable); decompress it first.");
        }

        if (XisoSplitter.IsSplitPath(isoPath))
        {
            throw new InvalidDataException(
                $"Cannot repair split part '{isoPath}' in place; reassemble the image first.");
        }

        var volInfo = XisoReader.GetVolumeInfo(isoPath);
        if (!volInfo.IsValid)
            throw new XisoFormatException($"Not a valid XISO: {isoPath}");

        var initial = XisoReader.AuditXiso(isoPath);
        if (volInfo is { RootDirSector: 0, RootDirSize: 0 })
        {
            // Empty volume: the audit passes by definition, nothing to walk.
            return new RepairResult([], initial.Issues, null, dryRun);
        }

        var fileLength = new FileInfo(isoPath).Length;
        var trustedAttrs = new HashSet<long>();
        var decidedKeys = new HashSet<string>(StringComparer.Ordinal);
        var fixedMessages = new List<string>();
        string? backupPath = null;
        var appliedAny = false;

        // Converge: fixing a directory's attribute byte makes its table
        // trustworthy, which can reveal fixable entries one level down.
        // Decided fixes are trusted immediately, so dry-run previews converge
        // exactly like real runs.
        for (var pass = 0; pass < MaxRepairPasses; pass++)
        {
            var entries = CollectEntries(isoPath, volInfo, fileLength, trustedAttrs);
            var fixes = DecideFixes(entries, trustedAttrs);

            // Missing optimized tag (the audit flags it; a too-short file
            // cannot hold one, so the fix is only offered when it fits).
            var tagFix = DecideTagFix(isoPath, fileLength);
            if (tagFix != null)
                fixes.Add(tagFix);

            var fresh = fixes.Where(f => decidedKeys.Add(f.Key)).ToList();
            if (fresh.Count == 0)
                break;

            fixedMessages.AddRange(fresh.Select(static f => f.Message));
            if (dryRun)
                continue;

            if (createBackup && backupPath == null)
            {
                // Keep the first backup: overwriting a previous `.old` would
                // destroy the true pre-repair original (BUG-LIB-027).
                backupPath = isoPath + ".old";
                if (!File.Exists(backupPath))
                    File.Copy(isoPath, backupPath);
            }

            using var fs = new FileStream(
                isoPath,
                new FileStreamOptions
                {
                    Mode = FileMode.Open, Access = FileAccess.ReadWrite, Share = FileShare.None, BufferSize = 65536
                });
            foreach (var fix in fresh)
                fix.Apply(fs);
            appliedAny = true;
        }

        if (!appliedAny)
            return new RepairResult(fixedMessages.ToArray(), initial.Issues, backupPath, dryRun);

        var reaudit = XisoReader.AuditXiso(isoPath);
        return new RepairResult(fixedMessages.ToArray(), reaudit.Issues, backupPath, dryRun);
    }

    /// <summary>A raw directory-table entry plus its on-disk patch offsets.</summary>
    /// <param name="AttrOffset">File offset of the attribute byte (entry start + 12).</param>
    /// <param name="RawAttrs">Attribute byte as stored.</param>
    /// <param name="NameOffset">File offset of the name bytes (entry start + 14).</param>
    /// <param name="Name">Decoded filename.</param>
    /// <param name="TableStart">File offset of the containing table (collision scope).</param>
    /// <param name="DirPath">Image-internal directory path (<c>/</c>, <c>/sub/</c>, …).</param>
    private sealed record RawEntry(
        long AttrOffset,
        byte RawAttrs,
        long NameOffset,
        string Name,
        long TableStart,
        string DirPath);

    /// <summary>A length-preserving patch plus its identity key and report line.</summary>
    private sealed record PendingFix(string Key, string Message, Action<FileStream> Apply);

    private static List<RawEntry> CollectEntries(
        string isoPath,
        VolumeInfo volInfo,
        long fileLength,
        HashSet<long> trustedAttrs)
    {
        var entries = new List<RawEntry>();
        using var fs = new FileStream(
            isoPath,
            new FileStreamOptions
            {
                Mode = FileMode.Open, Access = FileAccess.Read, Share = FileShare.Read, BufferSize = 65536
            });
        var rootDirStart = ((long)volInfo.RootDirSector * Constants.SectorSize) + volInfo.DiscLseek;
        if (rootDirStart < fileLength)
        {
            CollectWalk(fs, rootDirStart, rootDirStart, "/", fileLength, volInfo.DiscLseek,
                new HashSet<long>(), trustedAttrs, entries);
        }

        return entries;
    }

    /// <summary>
    /// Walks the directory tables exactly like the auditor (same sentinels and
    /// hardening bounds), collecting entries instead of issues. Any gate the
    /// auditor would trip simply ends that branch — the closing re-audit is the
    /// authority on what remains. Pointers from records with reserved attribute
    /// bits are not trusted (the record is corrupt; descending could wander
    /// into file data and invent phantom entries) unless this run already
    /// decided their attribute fix.
    /// </summary>
    private static void CollectWalk(
        FileStream fs,
        long dirStart,
        long tableStart,
        string path,
        long fileLength,
        long discLseek,
        HashSet<long> visited,
        HashSet<long> trustedAttrs,
        List<RawEntry> entries,
        // Recursion-depth bound (#16 hardening); kept explicit by design.
        // ReSharper disable once ParameterOnlyUsedForPreconditionCheck.Local
        int depth = 0)
    {
        if (depth > Constants.MaxTocDepth)
            return;

        var entriesInTable = 0;
        Span<byte> shortBuf = stackalloc byte[2];
        Span<byte> intBuf = stackalloc byte[4];
        Span<byte> byteBuf = stackalloc byte[1];
        Span<byte> headerRest = stackalloc byte[12];

        while (true)
        {
            if (++entriesInTable > Constants.MaxTocEntriesPerTable)
                return;

            if (dirStart >= fileLength || !visited.Add(dirStart))
                return;

            try
            {
                fs.Seek(dirStart, SeekOrigin.Begin);
                ReadExact(fs, shortBuf);
                var lOffset = BinaryPrimitives.ReadUInt16LittleEndian(shortBuf);

                if (lOffset == Constants.PadShort && dirStart == tableStart)
                    return;

                if (lOffset == Constants.EmptyDirectorySentinel && dirStart == tableStart)
                {
                    var peekPos = fs.Position;
                    var isAllZeros = false;
                    try
                    {
                        ReadExact(fs, headerRest);
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

                    fs.Seek(peekPos, SeekOrigin.Begin);
                    if (isAllZeros)
                        return;
                }

                if (lOffset != 0 && lOffset != Constants.PadShort)
                {
                    var leftSeek = tableStart + ((long)lOffset * Constants.DwordSize);
                    if (leftSeek >= 0 && leftSeek < fileLength)
                    {
                        CollectWalk(fs, leftSeek, tableStart, path, fileLength, discLseek,
                            new HashSet<long>(visited), trustedAttrs, entries, depth + 1);
                    }
                }

                fs.Seek(dirStart + 2, SeekOrigin.Begin);
                ReadExact(fs, shortBuf);
                var rOffset = BinaryPrimitives.ReadUInt16LittleEndian(shortBuf);

                ReadExact(fs, intBuf);
                var startSector = BinaryPrimitives.ReadUInt32LittleEndian(intBuf);

                ReadExact(fs, intBuf);
                var fileSize = BinaryPrimitives.ReadUInt32LittleEndian(intBuf);

                ReadExact(fs, byteBuf);
                var rawAttributes = byteBuf[0];

                ReadExact(fs, byteBuf);
                var filenameLength = byteBuf[0];

                var nameBuf = new byte[filenameLength];
                ReadExact(fs, nameBuf);
                var filename = Latin1Encoding.Instance.GetString(nameBuf);

                entries.Add(new RawEntry(dirStart + 12, rawAttributes, dirStart + 14, filename, tableStart, path));

                var attributes = Constants.MaskAttributes(rawAttributes);
                var trusted = (rawAttributes & Constants.AttributeReservedMask) == 0 ||
                              trustedAttrs.Contains(dirStart + 12);
                if (trusted && (attributes & Constants.AttributeDir) != 0 && fileSize > 0)
                {
                    var sectorOffset = ((long)startSector * Constants.SectorSize) + discLseek;
                    if (sectorOffset >= 0 && sectorOffset < fileLength)
                    {
                        CollectWalk(fs, sectorOffset, sectorOffset, path + filename + "/", fileLength, discLseek,
                            new HashSet<long>(), trustedAttrs, entries, depth + 1);
                    }
                }

                if (rOffset != 0 && rOffset != Constants.PadShort)
                {
                    var rightSeek = tableStart + ((long)rOffset * Constants.DwordSize);
                    if (rightSeek < 0 || rightSeek >= fileLength)
                        break;

                    dirStart = rightSeek;
                    continue;
                }

                break;
            }
            catch (IOException)
            {
                // Truncated entry header: class T, reported by the re-audit.
                return;
            }
        }
    }

    private static void ReadExact(FileStream fs, Span<byte> buffer)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = fs.Read(buffer[offset..]);
            if (read <= 0)
                throw new IOException($"Read error: expected {buffer.Length} bytes, got {offset}");

            offset += read;
        }
    }

    private static List<PendingFix> DecideFixes(List<RawEntry> entries, HashSet<long> trustedAttrs)
    {
        var fixes = new List<PendingFix>();

        // Collision scope is the containing table: group entries, seed each
        // table's name set with its current names, then apply renames in walk
        // order so two entries sanitizing to the same name collide safely.
        foreach (var table in entries.GroupBy(static e => e.TableStart))
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var e in table)
                names.Add(e.Name);

            foreach (var e in table)
            {
                if ((e.RawAttrs & Constants.AttributeReservedMask) != 0)
                {
                    var masked = Constants.MaskAttributes(e.RawAttrs);
                    var attrOffset = e.AttrOffset;
                    trustedAttrs.Add(attrOffset);
                    fixes.Add(new PendingFix(
                        $"attr:{attrOffset}",
                        $"'{e.DirPath}{e.Name}': cleared reserved attribute bits (0x{e.RawAttrs:X2} -> 0x{masked:X2})",
                        fs =>
                        {
                            fs.Seek(attrOffset, SeekOrigin.Begin);
                            fs.WriteByte(masked);
                        }));
                }

                if (e.Name.Contains('/') || e.Name.Contains('\\'))
                {
                    var sanitized = e.Name.Replace('/', '_').Replace('\\', '_');
                    names.Remove(e.Name);
                    if (names.Add(sanitized))
                    {
                        var nameBytes = Latin1Encoding.Instance.GetBytes(sanitized);
                        var nameOffset = e.NameOffset;
                        if (nameBytes.Length == Latin1Encoding.Instance.GetByteCount(e.Name))
                        {
                            var oldRef = $"{e.DirPath}{e.Name}";
                            fixes.Add(new PendingFix(
                                $"name:{nameOffset}",
                                $"'{oldRef}' renamed to '{sanitized}' (replaced path separator)",
                                fs =>
                                {
                                    fs.Seek(nameOffset, SeekOrigin.Begin);
                                    fs.Write(nameBytes, 0, nameBytes.Length);
                                }));
                        }
                        else
                        {
                            // Encoding did not round-trip length-preserving: restore
                            // the set and leave the entry for the re-audit.
                            names.Remove(sanitized);
                            names.Add(e.Name);
                        }
                    }
                    else
                    {
                        // Collision: another entry already holds the sanitized
                        // name — leave it; the re-audit reports the separator.
                        names.Add(e.Name);
                    }
                }
            }
        }

        return fixes;
    }

    private static PendingFix? DecideTagFix(string isoPath, long fileLength)
    {
        if (fileLength < Constants.OptimizedTagOffset + Constants.OptimizedTagLength)
            return null;

        try
        {
            using var fs = new FileStream(
                isoPath,
                new FileStreamOptions
                {
                    Mode = FileMode.Open, Access = FileAccess.Read, Share = FileShare.Read, BufferSize = 256
                });
            fs.Seek(Constants.OptimizedTagOffset, SeekOrigin.Begin);
            Span<byte> tagBuf = stackalloc byte[Constants.OptimizedTagLength];
            ReadExact(fs, tagBuf);
            var tag = Encoding.ASCII.GetString(tagBuf);
            if (tag.StartsWith(Constants.OptimizedTag[..Constants.OptimizedTagLengthMin], StringComparison.Ordinal))
                return null;
        }
        catch (IOException)
        {
            return null;
        }

        // Byte-identical to what XisoWriter stores at the same offset.
        var tagBytes = Encoding.ASCII.GetBytes(Constants.OptimizedTag);
        return new PendingFix(
            "tag",
            $"Wrote optimized tag at offset {Constants.OptimizedTagOffset}.",
            fs =>
            {
                fs.Seek(Constants.OptimizedTagOffset, SeekOrigin.Begin);
                fs.Write(tagBytes, 0, Constants.OptimizedTagLength);
            });
    }
}
