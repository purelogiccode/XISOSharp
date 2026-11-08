namespace XISOSharp;

/// <summary>
/// Turns host filesystem exceptions into actionable messages: names the path,
/// the operation that failed, and the likely fix. Raw <c>Permission denied</c>
/// or <c>Invalid argument</c> strings never have to reach the user alone.
/// </summary>
public static class HostIoDiagnostics
{
    private const int ErrorSharingViolation = 32;
    private const int ErrorLockViolation = 33;
    private const int ErrorFileExists = 80;
    private const int ErrorAlreadyExists = 183;
    private const int ErrorFilenameExcedRange = 206;

    /// <summary>
    /// Describes <paramref name="exception"/> with the failed operation and
    /// path, appending a likely fix for the common host errors.
    /// </summary>
    /// <param name="exception">The caught host exception.</param>
    /// <param name="operation">
    /// Short verb phrase describing the failed work, for example
    /// <c>"creating the image"</c> or <c>"writing the extracted file"</c>.
    /// </param>
    /// <param name="path">Host path involved, when known.</param>
    public static string Describe(Exception exception, string operation, string? path = null)
    {
        ArgumentNullException.ThrowIfNull(exception);
        string where = string.IsNullOrEmpty(path)
            ? operation
            : $"{operation} '{path}'";
        string detail = exception.Message.Trim();
        string normalized = detail.ToLowerInvariant();

        if (exception is UnauthorizedAccessException ||
            normalized.Contains("permission denied", StringComparison.Ordinal) ||
            normalized.Contains("access is denied", StringComparison.Ordinal) ||
            normalized.Contains("access denied", StringComparison.Ordinal))
        {
            return $"{where}: permission denied. Check the file/folder permissions and that no other " +
                   $"process holds the file open. ({detail})";
        }

        if (exception is PathTooLongException ||
            exception is IOException && HasHResult(exception, ErrorFilenameExcedRange) ||
            normalized.Contains("too long", StringComparison.Ordinal))
        {
            return $"{where}: the path is too long for this host filesystem. Shorten the destination " +
                   $"path or enable long-path support. ({detail})";
        }

        if (exception is IOException && HasHResult(exception, ErrorSharingViolation) ||
            exception is IOException && HasHResult(exception, ErrorLockViolation))
        {
            return $"{where}: another process is using the file. Close it and retry. ({detail})";
        }

        if (exception is IOException && HasHResult(exception, ErrorAlreadyExists) ||
            exception is IOException && HasHResult(exception, ErrorFileExists))
        {
            return $"{where}: the destination already exists. Remove it, choose another name, or pass " +
                   $"the overwrite option. ({detail})";
        }

        if (exception is ArgumentException and not ArgumentNullException ||
            exception is NotSupportedException ||
            normalized.Contains("invalid argument", StringComparison.Ordinal) ||
            normalized.Contains("invalid path", StringComparison.Ordinal))
        {
            return $"{where}: the host rejected the name or path. Remove illegal characters, avoid " +
                   $"reserved device names, or use the extract name policy to sanitize it. ({detail})";
        }

        if (exception is DirectoryNotFoundException)
        {
            return $"{where}: the destination directory does not exist and could not be created. " +
                   $"Check the drive and the path. ({detail})";
        }

        if (exception is FileNotFoundException)
        {
            return $"{where}: not found. Check the path and spelling. ({detail})";
        }

        if (exception is IOException && normalized.Contains("no space", StringComparison.Ordinal))
        {
            return $"{where}: the destination is full. Free space or choose another filesystem. ({detail})";
        }

        return $"{where}: {detail}";
    }

    private static bool HasHResult(Exception exception, int value)
    {
        int hresult = exception.HResult;
        // Windows HRESULTs carry the Win32 code in the low 16 bits; Unix
        // implementations expose errno, so only compare on Windows.
        return OperatingSystem.IsWindows() &&
               (hresult & 0xFFFF) == value;
    }
}
