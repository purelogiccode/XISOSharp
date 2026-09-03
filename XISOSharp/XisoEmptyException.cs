using XISOSharp.Models;

namespace XISOSharp;

/// <summary>
/// Exception thrown when an XISO image is structurally valid but contains
/// no files — both the root directory sector and size are zero.
/// </summary>
#pragma warning disable RCS1194 // Implement exception constructors — standard overloads are sufficient for modern .NET
public class XisoEmptyException : ExtractErrorException
{
    /// <summary>Creates a new <see cref="XisoEmptyException"/>.</summary>
    public XisoEmptyException() : base(ExtractError.ErrIsoNoFiles, "XISO image contains no files.")
    {
    }

    /// <summary>Creates a new <see cref="XisoEmptyException"/> with a message.</summary>
    /// <param name="message">Description of the empty-ISO condition.</param>
    public XisoEmptyException(string message) : base(ExtractError.ErrIsoNoFiles, message)
    {
    }

    /// <summary>Creates a new <see cref="XisoEmptyException"/> with a message and inner exception.</summary>
    /// <param name="message">Description of the empty-ISO condition.</param>
    /// <param name="innerException">The underlying cause.</param>
    public XisoEmptyException(string message, Exception innerException) : base(ExtractError.ErrIsoNoFiles, message,
        innerException)
    {
    }
}
#pragma warning restore RCS1194