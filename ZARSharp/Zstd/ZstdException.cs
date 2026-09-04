namespace ZARSharp.Zstd;

/// <summary>Thrown when zstd input is corrupt or uses unsupported features.</summary>
public sealed class ZstdException : Exception
{
    /// <summary>Creates a decoder error.</summary>
    /// <param name="message">Reason.</param>
    public ZstdException(string message)
        : base(message)
    {
    }

    /// <summary>Creates a decoder error with a default message.</summary>
    public ZstdException()
    {
    }

    /// <summary>Creates a decoder error with an inner cause.</summary>
    /// <param name="message">Reason.</param>
    /// <param name="innerException">Inner cause.</param>
    public ZstdException(string? message, Exception? innerException) : base(message, innerException)
    {
    }
}