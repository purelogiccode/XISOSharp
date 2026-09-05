namespace ZARSharp.Pipeline;

/// <summary>
/// Thrown when an archive input file cannot be opened for packing
/// (<c>zarchive.exe</c> exit <c>-15</c>: <c>Failed to open input file %s</c>).
/// </summary>
#pragma warning disable RCS1194 // Implement exception constructors - standard overloads are sufficient for modern .NET
public sealed class ZarInputOpenException : IOException
{
    /// <summary>Creates an input-open fault.</summary>
    /// <param name="message">Reason.</param>
    public ZarInputOpenException(string message)
        : base(message)
    {
    }

    /// <summary>Creates an input-open fault with a default message.</summary>
    public ZarInputOpenException()
    {
    }

    /// <summary>Creates an input-open fault with an inner cause.</summary>
    /// <param name="message">Reason.</param>
    /// <param name="innerException">Inner cause.</param>
    public ZarInputOpenException(string? message, Exception? innerException)
        : base(message, innerException)
    {
    }
}
#pragma warning restore RCS1194
