namespace ZARSharp;

/// <summary>
/// Directory pack / archive extract tool. Pure-C# port of
/// <c>src/main.cpp</c> (ZArchive 0.1.2 CLI). Uses exceptions instead of
/// process exit codes; refuse-overwrite and delete-incomplete-output
/// semantics are preserved.
/// </summary>
public static class ZArchiveTool
{
    /// <summary>Packs <paramref name="inputDirectory"/> into a new .zar file.</summary>
    /// <param name="inputDirectory">Directory to pack (recursively).</param>
    /// <param name="outputFile">
    /// Destination path, or null for <c>&lt;stem&gt;.zar</c> next to the input.
    /// </param>
    /// <param name="progress">Optional per-file callback (relative path).</param>
    /// <exception cref="IOException">On I/O errors or when refusing to overwrite.</exception>
    /// <exception cref="InvalidOperationException">On archive structure errors.</exception>
    public static void Pack(string inputDirectory, string? outputFile = null, Action<string>? progress = null)
    {
        if (!Directory.Exists(inputDirectory))
        {
            throw new DirectoryNotFoundException($"Input directory not found: {inputDirectory}");
        }

        outputFile ??= Path.Combine(
            Path.GetDirectoryName(Path.GetFullPath(inputDirectory)) ?? "",
            Path.GetFileNameWithoutExtension(inputDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)) + ".zar");

        if (File.Exists(outputFile) || Directory.Exists(outputFile))
        {
            throw new IOException($"The output file already exists: {outputFile}");
        }

        try
        {
            using var output = new FileStream(outputFile, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536);
            using var writer = new ZArchiveWriter(output);
            byte[] buffer = new byte[ZArchiveCommon.CompressedBlockSize];

            // Deterministic order (the C++ iterator order is unspecified).
            var entries = Directory.EnumerateFileSystemEntries(inputDirectory, "*", SearchOption.AllDirectories)
                .OrderBy(p => p, StringComparer.Ordinal)
                .ToList();

            foreach (string entry in entries)
            {
                string relative = Path.GetRelativePath(inputDirectory, entry).Replace('\\', '/');
                if (Directory.Exists(entry))
                {
                    if (!writer.MakeDir(relative, recursive: false))
                    {
                        throw new InvalidOperationException($"Failed to create directory {relative}");
                    }
                }
                else if (File.Exists(entry))
                {
                    progress?.Invoke(relative);
                    if (!writer.StartNewFile(relative))
                    {
                        throw new InvalidOperationException($"Failed to create archive file {relative}");
                    }

                    using var input = new FileStream(entry, FileMode.Open, FileAccess.Read, FileShare.Read, 65536);
                    int read;
                    while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        writer.AppendData(buffer.AsSpan(0, read));
                    }
                }
            }

            writer.Finalize();
        }
        catch
        {
            try { File.Delete(outputFile); } catch { /* best effort */ }

            throw;
        }
    }

    /// <summary>Extracts <paramref name="inputFile"/> into <paramref name="outputDirectory"/>.</summary>
    /// <exception cref="IOException">On I/O errors.</exception>
    /// <exception cref="InvalidOperationException">On corrupt archives.</exception>
    public static void Extract(string inputFile, string outputDirectory)
    {
        if (!File.Exists(inputFile))
        {
            throw new FileNotFoundException($"Unable to find archive file: {inputFile}");
        }

        using var reader = ZArchiveReader.TryOpen(inputFile) ??
            throw new InvalidOperationException("Failed to open ZArchive.");

        Directory.CreateDirectory(outputDirectory);
        ExtractRecursive(reader, string.Empty, outputDirectory);
    }

    private static void ExtractRecursive(ZArchiveReader reader, string srcPath, string outputDirectory)
    {
        uint dirHandle = reader.LookUp(srcPath);
        if (dirHandle == ZArchiveReader.InvalidNode || !reader.IsDirectory(dirHandle))
        {
            throw new InvalidOperationException($"Directory not found in archive: '{srcPath}'.");
        }

        Directory.CreateDirectory(outputDirectory);
        uint count = reader.GetDirEntryCount(dirHandle);
        for (uint i = 0; i < count; i++)
        {
            if (!reader.GetDirEntry(dirHandle, i, out ZArchiveReader.DirEntry entry))
            {
                throw new InvalidOperationException("Directory contains invalid node.");
            }

            string childSrc = string.IsNullOrEmpty(srcPath) ? entry.Name : srcPath + "/" + entry.Name;
            string childOut = Path.Combine(outputDirectory, entry.Name);
            if (entry.IsDirectory)
            {
                ExtractRecursive(reader, childSrc, childOut);
            }
            else
            {
                ExtractFile(reader, childSrc, childOut);
            }
        }
    }

    private static void ExtractFile(ZArchiveReader reader, string srcPath, string outputPath)
    {
        uint handle = reader.LookUp(srcPath);
        if (handle == ZArchiveReader.InvalidNode || !reader.IsFile(handle))
        {
            throw new InvalidOperationException($"Unable to extract file: {srcPath}");
        }

        using var output = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None, 65536);
        byte[] buffer = new byte[ZArchiveCommon.CompressedBlockSize];
        ulong offset = 0;
        while (true)
        {
            ulong read = reader.ReadFromFile(handle, offset, buffer);
            if (read == 0)
            {
                break;
            }

            output.Write(buffer, 0, (int)read);
            offset += read;
        }

        if (offset != reader.GetFileSize(handle))
        {
            throw new InvalidOperationException($"Extraction failed: {srcPath}");
        }
    }
}
