namespace XISOSharp;

#pragma warning disable RCS1194 // Implement exception constructors — standard overloads are sufficient for modern .NET

/// <summary>
/// A copy source ended before the requested byte count: truncated download,
/// torn image, or an entry pointing past end of image. Thrown by
/// <see cref="XisoFileCopier.CopyExact"/>; carries both counts so callers can
/// wrap it in their own typed error without losing information.
/// </summary>
public sealed class TruncatedCopyException : EndOfStreamException
{
    /// <summary>Bytes requested.</summary>
    public long ExpectedBytes { get; }

    /// <summary>Bytes delivered before the source ended.</summary>
    public long CopiedBytes { get; }

    /// <summary>Initializes the exception with the requested and delivered byte counts.</summary>
    public TruncatedCopyException(long expectedBytes, long copiedBytes)
        : base($"Source ended after {copiedBytes} of {expectedBytes} bytes.")
    {
        ExpectedBytes = expectedBytes;
        CopiedBytes = copiedBytes;
    }
}
#pragma warning restore RCS1194
