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
    private long _position;
    private bool _disposed;

    public CisoSplitOutput(string outputPath, long splitPoint)
    {
        _outputPath = outputPath ?? throw new ArgumentNullException(nameof(outputPath));
        if (splitPoint <= 0) throw new ArgumentOutOfRangeException(nameof(splitPoint));
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

    public override void Write(byte[] buffer, int offset, int count)
        => Write(buffer.AsSpan(offset, count));

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var written = 0;
        while (written < buffer.Length)
        {
            var globalPosition = _position + written;
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

        _position += buffer.Length;
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _position = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => _position + offset,
            _ => throw new NotSupportedException("SeekOrigin.End is not supported for split output")
        };
        return _position;
    }

    public override long Position
    {
        get => _position;
        set => _position = value;
    }

    public override long Length => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override bool CanRead => false;
    public override bool CanSeek => true;
    public override bool CanWrite => true;

    public override void Flush()
    {
        foreach (var part in _parts.Values) part.Flush();
    }

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
