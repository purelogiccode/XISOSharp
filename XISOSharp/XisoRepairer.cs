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
    /// A <c>.old</c> backup of the pre-repair image is written first (an existing
    /// backup is kept, not overwritten) unless disabled — the copy-in precedent
    /// (#22).
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
    /// The image is a CISO/CCI or GOD container (decompress first) or a split
    /// part (reassemble first); none is patch-stable in place.
    /// </exception>
    /// <exception cref="IOException">Thrown on read/write errors.</exception>
    public static RepairResult RepairInPlace(string isoPath, bool createBackup = true, bool dryRun = false)
    {
        ArgumentException.ThrowIfNullOrEmpty(isoPath);
        if (XisoImageSource.IsCompressedContainerPath(isoPath) || XisoImageSource.IsCompressedImage(isoPath))
        {
            throw new InvalidDataException(
                $"Cannot repair compressed CISO/CCI container '{isoPath}' in place (compressed bytes are not patch-stable); decompress it first.");
        }

        if (StfsSharp.GodHeader.IsGodContainer(isoPath))
        {
            throw new InvalidDataException(
                $"Cannot repair GOD container '{isoPath}' in place; decompress it to ISO first.");
        }

        if (StfsSharp.StfsPackage.IsStfsPackage(isoPath))
        {
            throw new InvalidDataException(
                $"Cannot repair STFS package '{isoPath}' in place; extract it first.");
        }

        if (XisoSplitter.IsSplitPath(isoPath))
        {
            throw new InvalidDataException(
                $"Cannot repair split part '{isoPath}' in place; reassemble the image first.");
        }

        VolumeInfo volInfo = XisoReader.GetVolumeInfo(isoPath);
        if (!volInfo.IsValid)
            throw new XisoFormatException($"Not a valid XISO: {isoPath}");

        AuditResult initial = XisoReader.AuditXiso(isoPath);
        if (volInfo is { RootDirSector: 0, RootDirSize: 0 })
        {
            // Empty volume: the audit passes by definition, nothing to walk.
            return new RepairResult([], initial.Issues, null, dryRun);
        }

        long fileLength = new FileInfo(isoPath).Length;
        HashSet<long> trustedAttrs = new();
        HashSet<string> decidedKeys = new(StringComparer.Ordinal);
        List<string> fixedMessages = new();
        string? backupPath = null;
        bool appliedAny = false;

        // Converge: fixing a directory's attribute byte makes its table
        // trustworthy, which can reveal fixable entries one level down.
        // Decided fixes are trusted immediately, so dry-run previews converge
        // exactly like real runs.
        for (int pass = 0; pass < MaxRepairPasses; pass++)
        {
            List<RawEntry> entries = CollectEntries(isoPath, volInfo, fileLength, trustedAttrs);
            List<PendingFix> fixes = DecideFixes(entries, trustedAttrs);

            // Missing optimized tag (the audit flags it; a too-short file
            // cannot hold one, so the fix is only offered when it fits).
            PendingFix? tagFix = DecideTagFix(isoPath, fileLength, volInfo.DiscLseek);
            if (tagFix != null)
                fixes.Add(tagFix);

            List<PendingFix> fresh = fixes.Where(f => decidedKeys.Add(f.Key)).ToList();
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

            using FileStream fs = new(
                isoPath,
                new FileStreamOptions
                {
                    Mode = FileMode.Open,
                    Access = FileAccess.ReadWrite,
                    Share = FileShare.None,
                    BufferSize = 65536
                });
            foreach (PendingFix fix in fresh)
                fix.Apply(fs);
            appliedAny = true;
        }

        if (!appliedAny)
            return new RepairResult(fixedMessages.ToArray(), initial.Issues, backupPath, dryRun);

        AuditResult reaudit = XisoReader.AuditXiso(isoPath);
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
        List<RawEntry> entries = new();
        using FileStream fs = new(
            isoPath,
            new FileStreamOptions
            {
                Mode = FileMode.Open,
                Access = FileAccess.Read,
                Share = FileShare.Read,
                BufferSize = 65536
            });
        long rootDirStart = ((long)volInfo.RootDirSector * Constants.SectorSize) + volInfo.DiscLseek;
        if (rootDirStart < fileLength)
        {
            CollectWalk(fs, rootDirStart, rootDirStart, volInfo.RootDirSize, "/", fileLength, volInfo.DiscLseek,
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
        long tableSize,
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
        // Subdirectory nesting stays recursive and bounded; intra-table AVL
        // depth is walked with the explicit in-order stack below, so a
        // pathological left-skewed table is repaired instead of being cut off
        // at MaxTocDepth (Phase 4 follow-up).
        if (depth > Constants.MaxTocDepth)
            return;

        int entriesInTable = 0;
        Span<byte> shortBuf = stackalloc byte[2];
        Span<byte> intBuf = stackalloc byte[4];
        Span<byte> byteBuf = stackalloc byte[1];
        Span<byte> headerRest = stackalloc byte[12];

        List<CollectEntry> pending = [];
        long? next = dirStart;

        while (true)
        {
            if (next is { } offset)
            {
                next = null;
                if (offset >= fileLength || offset >= tableStart + tableSize || !visited.Add(offset))
                    return;

                if (++entriesInTable > Constants.MaxTocEntriesPerTable)
                    return;

                try
                {
                    fs.Seek(offset, SeekOrigin.Begin);
                    ReadExact(fs, shortBuf);
                    ushort lOffset = BinaryPrimitives.ReadUInt16LittleEndian(shortBuf);

                    if (lOffset == Constants.PadShort && offset == tableStart)
                        return;

                    if (lOffset == Constants.EmptyDirectorySentinel && offset == tableStart)
                    {
                        long peekPos = fs.Position;
                        bool isAllZeros;
                        try
                        {
                            ReadExact(fs, headerRest);
                            isAllZeros = true;
                            foreach (byte b in headerRest)
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

                    bool descendLeft = false;
                    long leftSeek = 0;
                    if (lOffset != 0 && lOffset != Constants.PadShort)
                    {
                        leftSeek = tableStart + ((long)lOffset * Constants.DwordSize);
                        descendLeft = leftSeek >= 0 && leftSeek < fileLength && leftSeek < tableStart + tableSize;
                    }

                    fs.Seek(offset + 2, SeekOrigin.Begin);
                    ReadExact(fs, shortBuf);
                    ushort rOffset = BinaryPrimitives.ReadUInt16LittleEndian(shortBuf);

                    ReadExact(fs, intBuf);
                    uint startSector = BinaryPrimitives.ReadUInt32LittleEndian(intBuf);

                    ReadExact(fs, intBuf);
                    uint fileSize = BinaryPrimitives.ReadUInt32LittleEndian(intBuf);

                    ReadExact(fs, byteBuf);
                    byte rawAttributes = byteBuf[0];

                    ReadExact(fs, byteBuf);
                    byte filenameLength = byteBuf[0];

                    byte[] nameBuf = new byte[filenameLength];
                    ReadExact(fs, nameBuf);
                    string filename = Latin1Encoding.Instance.GetString(nameBuf);

                    CollectEntry entry = new(offset, rOffset, startSector, fileSize, rawAttributes, filename,
                        filename is "." or "..");

                    if (descendLeft)
                    {
                        // Collect the left subtree first (in-order); the entry is
                        // recorded when the descent unwinds.
                        pending.Add(entry);
                        if (pending.Count > Constants.MaxTocStackFrames)
                            return;

                        next = leftSeek;
                        continue;
                    }

                    CollectParsedEntry(fs, entry, tableStart, path, fileLength, discLseek, trustedAttrs, entries,
                        depth);

                    if (rOffset != 0 && rOffset != Constants.PadShort)
                    {
                        long rightSeek = tableStart + ((long)rOffset * Constants.DwordSize);
                        if (rightSeek < 0 || rightSeek >= fileLength || rightSeek >= tableStart + tableSize)
                            return;

                        next = rightSeek;
                        continue;
                    }
                }
                catch (IOException)
                {
                    // Truncated entry header: class T, reported by the re-audit.
                    return;
                }
            }

            bool resumed = false;
            while (pending.Count > 0)
            {
                CollectEntry entry = pending[^1];
                pending.RemoveAt(pending.Count - 1);
                try
                {
                    CollectParsedEntry(fs, entry, tableStart, path, fileLength, discLseek, trustedAttrs, entries,
                        depth);
                }
                catch (IOException)
                {
                    return;
                }

                if (entry.Right != 0 && entry.Right != Constants.PadShort)
                {
                    long rightSeek = tableStart + ((long)entry.Right * Constants.DwordSize);
                    if (rightSeek < 0 || rightSeek >= fileLength || rightSeek >= tableStart + tableSize)
                        return;

                    next = rightSeek;
                    resumed = true;
                    break;
                }
            }

            if (!resumed) return;
        }
    }

    /// <summary>
    /// Records one parsed directory entry (and, for a trusted subdirectory,
    /// walks its table recursively — bounded by <see cref="Constants.MaxTocDepth"/>).
    /// </summary>
    private static void CollectParsedEntry(
        FileStream fs,
        CollectEntry entry,
        long tableStart,
        string path,
        long fileLength,
        long discLseek,
        HashSet<long> trustedAttrs,
        List<RawEntry> entries,
        int depth)
    {
        // Structural "." / ".." records are skipped (children still walked).
        if (entry.Structural)
            return;

        entries.Add(new RawEntry(entry.Offset + 12, entry.RawAttributes, entry.Offset + 14, entry.Filename,
            tableStart, path));

        byte attributes = Constants.MaskAttributes(entry.RawAttributes);
        bool trusted = (entry.RawAttributes & Constants.AttributeReservedMask) == 0 ||
                       trustedAttrs.Contains(entry.Offset + 12);
        if (trusted && (attributes & Constants.AttributeDir) != 0 && entry.FileSize > 0)
        {
            long sectorOffset = ((long)entry.StartSector * Constants.SectorSize) + discLseek;
            if (sectorOffset >= 0 && sectorOffset < fileLength)
            {
                CollectWalk(fs, sectorOffset, sectorOffset, entry.FileSize, path + entry.Filename + "/",
                    fileLength, discLseek, new HashSet<long>(), trustedAttrs, entries, depth + 1);
            }
        }
    }

    /// <summary>One parsed table entry awaiting in-order collection.</summary>
    private readonly record struct CollectEntry(
        long Offset,
        ushort Right,
        uint StartSector,
        uint FileSize,
        byte RawAttributes,
        string Filename,
        bool Structural);

    private static void ReadExact(FileStream fs, Span<byte> buffer)
    {
        int offset = 0;
        while (offset < buffer.Length)
        {
            int read = fs.Read(buffer[offset..]);
            if (read <= 0)
                throw new IOException($"Read error: expected {buffer.Length} bytes, got {offset}");

            offset += read;
        }
    }

    private static List<PendingFix> DecideFixes(List<RawEntry> entries, HashSet<long> trustedAttrs)
    {
        List<PendingFix> fixes = new();

        // Collision scope is the containing table: group entries, seed each
        // table's name set with its current names, then apply renames in walk
        // order so two entries sanitizing to the same name collide safely.
        foreach (IGrouping<long, RawEntry> table in entries.GroupBy(static e => e.TableStart))
        {
            // XISO names are case-insensitive, so a rename collision check must
            // be too: `A/B` -> `A_B` collides with an existing `a_b`.
            HashSet<string> names = new(StringComparer.OrdinalIgnoreCase);
            foreach (RawEntry e in table)
                names.Add(e.Name);

            foreach (RawEntry e in table)
            {
                if ((e.RawAttrs & Constants.AttributeReservedMask) != 0)
                {
                    byte masked = Constants.MaskAttributes(e.RawAttrs);
                    long attrOffset = e.AttrOffset;
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

                if (XisoEntryNames.IsInvalidEntryName(e.Name))
                {
                    string sanitized = XisoEntryNames.SanitizeEntryName(e.Name);
                    names.Remove(e.Name);
                    if (string.Equals(sanitized, e.Name, StringComparison.Ordinal))
                    {
                        // The sanitizer preserved the name: nothing to patch.
                        names.Add(e.Name);
                    }
                    else if (names.Add(sanitized))
                    {
                        byte[] nameBytes = Latin1Encoding.Instance.GetBytes(sanitized);
                        long nameOffset = e.NameOffset;
                        if (nameBytes.Length == Latin1Encoding.Instance.GetByteCount(e.Name))
                        {
                            string oldRef = $"{e.DirPath}{e.Name}";
                            fixes.Add(new PendingFix(
                                $"name:{nameOffset}",
                                $"'{oldRef}' renamed to '{sanitized}' (sanitized invalid character(s))",
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

    private static PendingFix? DecideTagFix(string isoPath, long fileLength, long discLseek)
    {
        // The tag lives at partition offset 31337, so offset (Redump/XGD)
        // images carry it at discLseek + 31337 — the same location the audit
        // probes. Writing the absolute offset would corrupt unrelated bytes.
        long tagOffset = discLseek + Constants.OptimizedTagOffset;
        if (fileLength < tagOffset + Constants.OptimizedTagLength)
            return null;

        try
        {
            using FileStream fs = new(
                isoPath,
                new FileStreamOptions
                {
                    Mode = FileMode.Open,
                    Access = FileAccess.Read,
                    Share = FileShare.Read,
                    BufferSize = 256
                });
            fs.Seek(tagOffset, SeekOrigin.Begin);
            Span<byte> tagBuf = stackalloc byte[Constants.OptimizedTagLength];
            ReadExact(fs, tagBuf);
            string tag = Encoding.ASCII.GetString(tagBuf);
            if (tag.StartsWith(Constants.OptimizedTag[..Constants.OptimizedTagLengthMin], StringComparison.Ordinal))
                return null;
        }
        catch (IOException)
        {
            return null;
        }

        // Byte-identical to what XisoWriter stores at the same offset.
        byte[] tagBytes = Encoding.ASCII.GetBytes(Constants.OptimizedTag);
        return new PendingFix(
            "tag",
            $"Wrote optimized tag at offset {tagOffset}.",
            fs =>
            {
                fs.Seek(tagOffset, SeekOrigin.Begin);
                fs.Write(tagBytes, 0, Constants.OptimizedTagLength);
            });
    }
}
