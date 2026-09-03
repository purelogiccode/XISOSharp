using XISOSharp.Models;

namespace XISOSharp;

/// <summary>
/// Exception thrown for non-fatal XISO extraction errors such as an empty ISO image.
/// The <see cref="ErrorCode"/> property identifies the specific error.
/// </summary>
public class ExtractErrorException : Exception
{
    /// <summary>The specific error code that caused this exception.</summary>
    public ExtractError ErrorCode { get; }

    /// <summary>Creates a new <see cref="ExtractErrorException"/> with no error code.</summary>
    public ExtractErrorException()
    {
    }

    /// <summary>Creates a new <see cref="ExtractErrorException"/> with a message.</summary>
    /// <param name="message">The error message.</param>
    public ExtractErrorException(string message) : base(message)
    {
    }

    /// <summary>Creates a new <see cref="ExtractErrorException"/> with a message and inner exception.</summary>
    /// <param name="message">The error message.</param>
    /// <param name="innerException">The inner exception.</param>
    public ExtractErrorException(string message, Exception innerException) : base(message, innerException)
    {
    }

    /// <summary>
    /// Creates a new <see cref="ExtractErrorException"/> with the given error code.
    /// </summary>
    /// <param name="code">The <see cref="ExtractError"/> value describing the failure.</param>
    public ExtractErrorException(ExtractError code) : base($"Extract error: {code}")
    {
        ErrorCode = code;
    }

    /// <summary>Creates a new <see cref="ExtractErrorException"/> with a message and error code.</summary>
    /// <param name="code">The <see cref="ExtractError"/> value describing the failure.</param>
    /// <param name="message">The error message.</param>
    public ExtractErrorException(ExtractError code, string message) : base(message)
    {
        ErrorCode = code;
    }

    /// <summary>Creates a new <see cref="ExtractErrorException"/> with an error code, message, and inner exception.</summary>
    /// <param name="code">The <see cref="ExtractError"/> value describing the failure.</param>
    /// <param name="message">The error message.</param>
    /// <param name="innerException">The inner exception.</param>
    public ExtractErrorException(ExtractError code, string message, Exception innerException) : base(message,
        innerException)
    {
        ErrorCode = code;
    }
}