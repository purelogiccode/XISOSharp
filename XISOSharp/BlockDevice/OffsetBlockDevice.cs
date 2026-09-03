namespace XISOSharp.BlockDevice;

/// <summary>
/// Offset wrapper around an inner <see cref="IBlockDevice"/>, mirroring
/// <c>xdvdfs-core/src/blockdev.rs::OffsetWrapper</c> (and <c>OffsetWrapper::new</c> probing).
/// Used for Redump skip-sectors and CISO decompression views.
/// </summary>
public sealed class OffsetBlockDevice : IBlockDevice
{
    private readonly bool _leaveOpen;

    /// <summary>Creates an offset view starting at <paramref name="offset"/> bytes into <paramref name="inner"/>.</summary>
    public OffsetBlockDevice(IBlockDevice inner, long offset, bool leaveOpen = false)
    {
        Inner = inner ?? throw new ArgumentNullException(nameof(inner));
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        Offset = offset;
        _leaveOpen = leaveOpen;
    }

    /// <summary>Byte offset of this view within the inner device.</summary>
    public long Offset { get; }

    /// <summary>Inner device.</summary>
    public IBlockDevice Inner { get; }

    /// <inheritdoc/>
    public long Length
    {
        get
        {
            var innerLen = Inner.Length;
            var len = innerLen - Offset;
            return len < 0 ? 0 : len;
        }
    }

    /// <inheritdoc/>
    public int Read(long offset, Span<byte> buffer)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        return Inner.Read(Offset + offset, buffer);
    }

    /// <inheritdoc/>
    public void Write(long offset, ReadOnlySpan<byte> buffer)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        Inner.Write(Offset + offset, buffer);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (!_leaveOpen) Inner.Dispose();
    }

    /// <summary>
    /// Probes known XDVDFS offsets (0, Global, XGD3, Hybrid, XGD1) similar to
    /// <c>OffsetWrapper::new</c> in <c>blockdev.rs</c>, returning the first
    /// view whose header validates, or throws if none match.
    /// </summary>
    public static OffsetBlockDevice Probe(IBlockDevice inner, string isoName)
    {
        long[] offsets =
        [
            0, Constants.GlobalLseekOffset, Constants.Xgd3LseekOffset, Constants.Xgd2HybridLseekOffset,
            Constants.Xgd1LseekOffset
        ];
        Span<byte> buf = stackalloc byte[Constants.HeaderDataLength];
        var magic = System.Text.Encoding.ASCII.GetBytes(Constants.HeaderData);
        foreach (var off in offsets)
        {
            var view = new OffsetBlockDevice(inner, off, leaveOpen: true);
            try
            {
                // Try to validate header at HeaderOffset within view
                var n = view.Read(Constants.HeaderOffset, buf);
                if (n != Constants.HeaderDataLength) continue;
                if (buf.SequenceEqual(magic))
                    return view;
            }
            catch
            {
                // ignored
            }

            view.Dispose();
        }

        throw new XisoFormatException($"Invalid XISO: {isoName} — no header found at any known offset");
    }
}