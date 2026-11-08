namespace XISOSharp;

/// <summary>
/// Name rules shared by the writer, patcher and skeleton overlay: XDVDFS stores
/// Latin-1 bytes, and Xbox tooling expects each path component to fit the
/// classic 42-character limit (extract-xiso #74/#81).
/// </summary>
public static class XisoNameRules
{
    /// <summary>
    /// Maximum length of one XDVDFS path component accepted by Xbox and Xbox
    /// 360 tooling. XDVDFS itself stores a one-byte length, but the console
    /// filesystem rejects longer components.
    /// </summary>
    public const int MaxComponentLength = 42;

    /// <summary>
    /// Returns <c>null</c> when <paramref name="component"/> can be stored in an
    /// XDVDFS directory entry; otherwise returns an actionable error message.
    /// </summary>
    /// <param name="component">One path component (no separators).</param>
    public static string? GetComponentError(string component)
    {
        ArgumentNullException.ThrowIfNull(component);
        if (component.Length == 0)
        {
            return "Filename component is empty.";
        }

        if (component.Contains('/') || component.Contains('\\'))
        {
            return $"Filename '{component}' contains a path separator; one directory entry holds one component.";
        }

        foreach (char c in component)
        {
            if (c > 0xFF)
            {
                return $"Filename '{component}' contains U+{(int)c:X4}, which is outside the Latin-1/Windows-1252 " +
                       "range XDVDFS stores.";
            }
        }

        if (component.Length > MaxComponentLength)
        {
            return $"Filename component '{component}' is {component.Length} characters; the Xbox limit is " +
                   $"{MaxComponentLength} characters.";
        }

        return null;
    }

    /// <summary>
    /// Validates every component of an image-internal path (leading/trailing
    /// separators are ignored).
    /// </summary>
    /// <param name="internalPath">Path using <c>/</c> or <c>\</c> separators.</param>
    /// <returns>The first actionable error, or <c>null</c> when every component is valid.</returns>
    public static string? GetPathError(string internalPath)
    {
        ArgumentNullException.ThrowIfNull(internalPath);
        foreach (string component in internalPath.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries))
        {
            string? error = GetComponentError(component);
            if (error != null)
            {
                return error;
            }
        }

        return null;
    }

    /// <summary>
    /// Returns <c>true</c> when <paramref name="component"/> fits the Xbox
    /// 42-character limit and is Latin-1 representable.
    /// </summary>
    /// <param name="component">One path component.</param>
    public static bool IsValidComponent(string component) => GetComponentError(component) is null;
}
