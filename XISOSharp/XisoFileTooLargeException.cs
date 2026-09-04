namespace XISOSharp;

/// <summary>
/// Exception thrown when a file exceeds the maximum size supported by the XISO
/// format. The on-disk file-size field is a 32-bit unsigned integer, so individual
/// files cannot exceed 4,294,967,295 bytes (~4 GB).
/// </summary>
#pragma warning disable RCS1194 // Implement exception constructors — standard overloads are sufficient for modern .NET
public class XisoFileTooLargeException : IOException
{
    /// <summary>The filename of the oversized file.</summary>
    public string FileName { get; }

    /// <summary>The file size in bytes that exceeded the limit.</summary>
    public long FileSize { get; }

    /// <summary>Creates a new <see cref="XisoFileTooLargeException"/>.</summary>
    public XisoFileTooLargeException() : base("A file is too large for XISO (exceeds the 4 GB limit).")
    {
        FileName = "";
        FileSize = 0;
    }

    /// <summary>Creates a new <see cref="XisoFileTooLargeException"/> with a message.</summary>
    /// <param name="message">Description of the error.</param>
    public XisoFileTooLargeException(string message) : base(message)
    {
        FileName = "";
        FileSize = 0;
    }

    /// <summary>Creates a new <see cref="XisoFileTooLargeException"/> with a message and inner exception.</summary>
    /// <param name="message">Description of the error.</param>
    /// <param name="innerException">The underlying cause.</param>
    public XisoFileTooLargeException(string message, Exception innerException) : base(message, innerException)
    {
        FileName = "";
        FileSize = 0;
    }

    /// <summary>Creates a new <see cref="XisoFileTooLargeException"/> with filename and size details.</summary>
    /// <param name="fileName">Filename of the oversized file.</param>
    /// <param name="fileSize">Actual file size in bytes.</param>
    public XisoFileTooLargeException(string fileName, long fileSize)
        : base($"File '{fileName}' is too large for XISO ({fileSize:N0} bytes exceeds the 4 GB limit).")
    {
        FileName = fileName;
        FileSize = fileSize;
    }

    /// <summary>Creates a new <see cref="XisoFileTooLargeException"/> with a message and HRESULT.</summary>
    /// <param name="message">Description of the error.</param>
    /// <param name="hresult">The HRESULT error code.</param>
    public XisoFileTooLargeException(string? message, int hresult) : base(message, hresult)
    {
        FileName = "";
        FileSize = 0;
    }
}
#pragma warning restore RCS1194