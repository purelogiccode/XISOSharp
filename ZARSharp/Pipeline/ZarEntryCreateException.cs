namespace ZARSharp.Pipeline;

/// <summary>
/// Thrown when an archive entry cannot be created while packing
/// (<c>zarchive.exe</c> exit <c>-14</c>: <c>Failed to create archive file
/// %s</c>).
/// </summary>
public sealed class ZarEntryCreateException : InvalidOperationException
{
    /// <summary>Creates an entry-create fault.</summary>
    /// <param name="message">Reason.</param>
    public ZarEntryCreateException(string message)
        : base(message)
    {
    }

    /// <summary>Creates an entry-create fault with a default message.</summary>
    public ZarEntryCreateException()
    {
    }

    /// <summary>Creates an entry-create fault with an inner cause.</summary>
    /// <param name="message">Reason.</param>
    /// <param name="innerException">Inner cause.</param>
    public ZarEntryCreateException(string? message, Exception? innerException)
        : base(message, innerException)
    {
    }
}
