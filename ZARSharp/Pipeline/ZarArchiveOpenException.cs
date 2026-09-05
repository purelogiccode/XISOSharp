namespace ZARSharp.Pipeline;

/// <summary>
/// Thrown when an archive file cannot be opened for extraction
/// (<c>zarchive.exe</c> exit <c>-11</c>: <c>Failed to open ZArchive</c>).
/// </summary>
public sealed class ZarArchiveOpenException : InvalidOperationException
{
    /// <summary>Creates an archive-open fault.</summary>
    /// <param name="message">Reason.</param>
    public ZarArchiveOpenException(string message)
        : base(message)
    {
    }

    /// <summary>Creates an archive-open fault with a default message.</summary>
    public ZarArchiveOpenException()
    {
    }

    /// <summary>Creates an archive-open fault with an inner cause.</summary>
    /// <param name="message">Reason.</param>
    /// <param name="innerException">Inner cause.</param>
    public ZarArchiveOpenException(string? message, Exception? innerException)
        : base(message, innerException)
    {
    }
}
