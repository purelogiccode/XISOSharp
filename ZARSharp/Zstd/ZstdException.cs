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
}