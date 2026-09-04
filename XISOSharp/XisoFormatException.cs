namespace XISOSharp;

/// <summary>
/// Exception thrown when an XISO image has an invalid format — for example,
/// a missing or corrupt header magic, a truncated image, or sector pointers
/// that exceed the file bounds.
/// </summary>
#pragma warning disable RCS1194 // Implement exception constructors — standard (string) and (string, Exception) are sufficient for modern .NET
public class XisoFormatException : IOException
{
    /// <summary>Creates a new <see cref="XisoFormatException"/>.</summary>
    public XisoFormatException()
    {
    }

    /// <summary>Creates a new <see cref="XisoFormatException"/> with a message.</summary>
    /// <param name="message">Description of the format error.</param>
    public XisoFormatException(string message) : base(message)
    {
    }

    /// <summary>Creates a new <see cref="XisoFormatException"/> with a message and inner exception.</summary>
    /// <param name="message">Description of the format error.</param>
    /// <param name="innerException">The underlying cause.</param>
    public XisoFormatException(string message, Exception innerException) : base(message, innerException)
    {
    }

    /// <summary>Creates a new <see cref="XisoFormatException"/> with a message and HRESULT.</summary>
    /// <param name="message">Description of the format error.</param>
    /// <param name="hresult">The HRESULT error code.</param>
    public XisoFormatException(string? message, int hresult) : base(message, hresult)
    {
    }
}
#pragma warning restore RCS1194