namespace XISOSharp;

/// <summary>
/// Write-side of split CSO output (<c>ciso::split::SplitOutput</c>): a seekable write-only
/// stream that fans writes out to numbered part files (<c>&lt;base&gt;.&lt;n&gt;.cso</c>) at their
/// absolute positions, creating parts on demand.
/// </summary>
internal sealed class CisoSplitOutput : Stream
{
    private readonly string _outputPath;
    private readonly long _splitPoint;
    private readonly Dictionary<long, FileStream> _parts = [];
    private readonly List<string> _partPaths = [];
    private bool _disposed;

    /// <summary>
    /// Creates a write-only stream that fans writes out to numbered part files at their
    /// absolute positions, creating parts on demand.
    /// </summary>
    /// <param name="outputPath">Base output path used to derive part file names.</param>
    /// <param name="splitPoint">Global byte threshold at which a new part starts; must be positive.</param>
    public CisoSplitOutput(string outputPath, long splitPoint)
    {
        _outputPath = outputPath ?? throw new ArgumentNullException(nameof(outputPath));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(splitPoint);
        _splitPoint = splitPoint;
    }

    /// <summary>Part file paths in creation order (ascending part index).</summary>
    public IReadOnlyList<string> PartPaths => _partPaths;

    private FileStream GetPart(long partIndex)
    {
        if (_parts.TryGetValue(partIndex, out var part)) return part;

        var path = CisoSplitFile.PartPath(_outputPath, partIndex);
        part = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 65536);
        _parts[partIndex] = part;
        _partPaths.Add(path);
        return part;
    }

    /// <summary>Writes bytes at the current global <see cref="Position"/>, splitting across parts as needed.</summary>
    /// <param name="buffer">Source array.</param>
    /// <param name="offset">Offset in <paramref name="buffer"/> to read from.</param>
    /// <param name="count">Number of bytes to write.</param>
    public override void Write(byte[] buffer, int offset, int count)
    {
        Write(buffer.AsSpan(offset, count));
    }

    /// <summary>Writes bytes at the current global <see cref="Position"/>, splitting across parts as needed.</summary>
    /// <param name="buffer">Source span.</param>
    public override void Write(ReadOnlySpan<byte> buffer)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var written = 0;
        while (written < buffer.Length)
        {
            var globalPosition = Position + written;
            var handle = GetPart(globalPosition / _splitPoint);

            // Bytes remaining to the split point (a write starting exactly on the boundary
            // fills a whole part before splitting again, as in SplitOutput).
            var bytesToSplit = globalPosition % _splitPoint;
            if (bytesToSplit == 0) bytesToSplit = _splitPoint;

            var toWrite = (int)Math.Min(buffer.Length - written, bytesToSplit);
            handle.Seek(globalPosition, SeekOrigin.Begin);
            handle.Write(buffer.Slice(written, toWrite));
            written += toWrite;
        }

        Position += buffer.Length;
    }

    /// <summary>Seeks to a global write position. <see cref="SeekOrigin.End"/> is not supported.</summary>
    /// <param name="offset">Offset relative to <paramref name="origin"/>.</param>
    /// <param name="origin">Reference point: begin or current.</param>
    /// <returns>The new global position.</returns>
    /// <exception cref="NotSupportedException">Thrown for <see cref="SeekOrigin.End"/>.</exception>
    public override long Seek(long offset, SeekOrigin origin)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Position = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => Position + offset,
            _ => throw new NotSupportedException("SeekOrigin.End is not supported for split output")
        };
        return Position;
    }

    /// <summary>Gets or sets the global write position across all parts.</summary>
    public override long Position { get; set; }

    /// <summary>Not supported; the composite length is not tracked for write-only split output.</summary>
    /// <exception cref="NotSupportedException">Always thrown.</exception>
    public override long Length => throw new NotSupportedException();

    /// <summary>Not supported; parts grow on demand via writes and seeks.</summary>
    /// <param name="value">Unused.</param>
    /// <exception cref="NotSupportedException">Always thrown.</exception>
    public override void SetLength(long value)
    {
        throw new NotSupportedException();
    }

    /// <summary>Not supported; the split output stream is write-only.</summary>
    /// <param name="buffer">Unused.</param>
    /// <param name="offset">Unused.</param>
    /// <param name="count">Unused.</param>
    /// <returns>Never returns.</returns>
    /// <exception cref="NotSupportedException">Always thrown.</exception>
    public override int Read(byte[] buffer, int offset, int count)
    {
        throw new NotSupportedException();
    }

    /// <summary>Gets a value indicating whether reading is supported (always <c>false</c>).</summary>
    public override bool CanRead => false;

    /// <summary>Gets a value indicating whether seeking is supported (always <c>true</c>).</summary>
    public override bool CanSeek => true;

    /// <summary>Gets a value indicating whether writing is supported (always <c>true</c>).</summary>
    public override bool CanWrite => true;

    /// <summary>Flushes all created part files.</summary>
    public override void Flush()
    {
        foreach (var part in _parts.Values) part.Flush();
    }

    /// <summary>Disposes all created part streams.</summary>
    /// <param name="disposing">Whether managed resources should be disposed.</param>
    protected override void Dispose(bool disposing)
    {
        if (_disposed) return;
        _disposed = true;
        if (disposing)
        {
            foreach (var part in _parts.Values) part.Dispose();
            _parts.Clear();
        }

        base.Dispose(disposing);
    }
}