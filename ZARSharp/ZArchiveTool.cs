namespace ZARSharp;

using ZARSharp.Pipeline;

/// <summary>
/// Directory pack / archive extract tool. Pure-C# port of
/// <c>src/main.cpp</c> (ZArchive 0.1.2 CLI). Uses exceptions instead of
/// process exit codes; refuse-overwrite and delete-incomplete-output
/// semantics are preserved. The loops live in the shared
/// <see cref="ZarPackEngine"/>; this class keeps the exact
/// <c>main.cpp</c>-compatible surface.
/// </summary>
public static class ZArchiveTool
{
    /// <summary>
    /// Packs <paramref name="inputDirectory"/> into a new .zar file, compressing
    /// every 64 KiB block (zstd level 6 by default).
    /// </summary>
    /// <param name="inputDirectory">Directory to pack (recursively).</param>
    /// <param name="outputFile">
    /// Destination path, or null for <c>&lt;stem&gt;.zar</c> next to the input.
    /// </param>
    /// <param name="progress">Optional per-file callback (relative path).</param>
    /// <param name="compressor">
    /// Block compressor, or null for the default <see cref="Zstd.ZstdCompressor"/>
    /// (level 6). Pass <c>new ZarRawCompressor()</c> to store blocks raw.
    /// </param>
    /// <param name="deterministicOrder">
    /// True (default) sorts entries ordinally for reproducible archives.
    /// False preserves native filesystem enumeration order, mirroring
    /// <c>recursive_directory_iterator</c> for byte-parity runs.
    /// </param>
    /// <exception cref="IOException">On I/O errors or when refusing to overwrite.</exception>
    /// <exception cref="InvalidOperationException">On archive structure errors.</exception>
    public static void Pack(
        string inputDirectory, string? outputFile = null, Action<string>? progress = null,
        IZarBlockCompressor? compressor = null, bool deterministicOrder = true)
    {
        var options = new ZarPipelineOptions
        {
            Compressor = compressor,
            DeterministicOrder = deterministicOrder,
            CollisionPolicy = ZarCollisionPolicy.Fail,
        };
        IProgress<ZarProgress>? sink = progress == null ? null : new ActionProgress(progress);
        // Fail policy never skips, so the result is always non-null here.
        ZarPipeline.Pack(inputDirectory, outputFile, options, sink);
    }

    /// <summary>Adapts a per-file <see cref="Action{T}"/> to <see cref="IProgress{T}"/>.</summary>
    private sealed class ActionProgress(Action<string> action) : IProgress<ZarProgress>
    {
        private string _last = string.Empty;

        public void Report(ZarProgress value)
        {
            if (value.CurrentFile.Length != 0 &&
                !string.Equals(value.CurrentFile, _last, StringComparison.Ordinal))
            {
                _last = value.CurrentFile;
                action(value.CurrentFile);
            }
        }
    }

    /// <summary>Extracts <paramref name="inputFile"/> into <paramref name="outputDirectory"/>.</summary>
    /// <exception cref="IOException">On I/O errors.</exception>
    /// <exception cref="InvalidOperationException">On corrupt archives.</exception>
    public static void Extract(string inputFile, string outputDirectory)
    {
        ZarPipeline.Extract(inputFile, outputDirectory);
    }
}