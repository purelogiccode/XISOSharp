namespace XISOSharp;

using System.Buffers;

/// <summary>
/// Scenario-tuned copy core for extraction (TODO #8, xdvdfs #167).
/// Unifies the three formerly separate copy loops (<c>ExtractFile</c>,
/// <c>CopyOutFile</c>, <c>ComputeFileHash</c>) behind one exact-byte copier:
/// <list type="bullet">
/// <item>Small files complete in a single chunk read (no loop overhead).</item>
/// <item>Large files stream in buffer-sized chunks with per-chunk cancellation
/// and byte-level progress, so UI consumers get smooth progress bars.</item>
/// <item>Callers share one reusable buffer (no per-file 2 MB LOH allocation);
/// direct users without a buffer get a pooled one.</item>
/// </list>
/// The sink is caller-supplied (<c>onChunk</c>), so file writes and hashing
/// share the loop while keeping their own error types.
/// </summary>
public static class XisoFileCopier
{
    /// <summary>
    /// Largest chunk rented from the shared <see cref="ArrayPool{T}"/> when the
    /// caller passes no buffer. Matches the pool's bucket ceiling so rented
    /// arrays are actually pooled instead of becoming LOH one-offs.
    /// </summary>
    public const int MaxPooledChunkSize = 1024 * 1024;

    /// <summary>
    /// Copies exactly <paramref name="byteCount"/> bytes from
    /// <paramref name="source"/>, invoking <paramref name="onChunk"/> for each
    /// chunk read. Short reads are stitched; a source that ends early throws
    /// <see cref="TruncatedCopyException"/> carrying the counts.
    /// </summary>
    /// <param name="source"> positioned source stream.</param>
    /// <param name="byteCount">Exact number of bytes to copy; 0 copies nothing and invokes neither callback.</param>
    /// <param name="onChunk">Sink invoked as <c>onChunk(buffer, count)</c>; exceptions propagate unwrapped.</param>
    /// <param name="buffer">
    /// Scratch buffer, or <c>null</c> to rent a pooled one sized to the copy
    /// (up to <see cref="MaxPooledChunkSize"/>). Must not be empty.
    /// </param>
    /// <param name="onProgress">Invoked with the cumulative bytes copied after each chunk.</param>
    /// <param name="cancellationToken">Checked before every chunk.</param>
    /// <returns>Total bytes copied (always <paramref name="byteCount"/>).</returns>
    /// <exception cref="TruncatedCopyException">The source ended before <paramref name="byteCount"/> bytes.</exception>
    public static long CopyExact(
        Stream source,
        long byteCount,
        Action<byte[], int> onChunk,
        byte[]? buffer = null,
        Action<long>? onProgress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(onChunk);
        ArgumentOutOfRangeException.ThrowIfNegative(byteCount);
        if (buffer?.Length == 0)
            throw new ArgumentException("Buffer must not be empty.", nameof(buffer));

        if (byteCount == 0)
            return 0;

        byte[]? rented = null;
        try
        {
            var buf = buffer ?? (rented =
                ArrayPool<byte>.Shared.Rent((int)Math.Min(byteCount, MaxPooledChunkSize)));
            long totalCopied = 0;
            while (totalCopied < byteCount)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var toRead = (int)Math.Min(byteCount - totalCopied, buf.Length);
                var read = source.Read(buf, 0, toRead);
                if (read <= 0)
                    throw new TruncatedCopyException(byteCount, totalCopied);
                onChunk(buf, read);
                totalCopied += read;
                onProgress?.Invoke(totalCopied);
            }

            return totalCopied;
        }
        finally
        {
            if (rented is not null)
                ArrayPool<byte>.Shared.Return(rented);
        }
    }
}
