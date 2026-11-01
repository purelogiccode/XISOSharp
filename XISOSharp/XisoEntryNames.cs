namespace XISOSharp;

/// <summary>
/// Host-safety rules for XISO entry names. Image names are Latin1 byte strings
/// that may contain characters the host filesystem treats specially: path
/// separators, the drive-relative colon on Windows (<c>C:evil</c> would resolve
/// against another directory), and trailing dots/spaces that Win32 strips
/// during path normalization (<c>".. "</c> normalizes to <c>".."</c>). Used by
/// the extract walkers (reject), the salvager/repairer (sanitize) and the
/// auditor (report) so every path agrees on what is representable. Other
/// host-invalid characters (NUL, <c>&lt;</c>, …) fail per-file at create time
/// with a named error instead of aborting the whole walk.
/// </summary>
internal static class XisoEntryNames
{
    /// <summary>
    /// Returns true when <paramref name="name"/> is invalid for every walker,
    /// on every host: an empty name or one carrying a path separator. Exact
    /// <c>"."</c>/<c>".."</c> records are not flagged — walkers skip those
    /// separately. Rewrite/archive paths (which never build host paths) must
    /// use this check so a structurally readable image stays readable on
    /// Windows, matching the reference tool.
    /// </summary>
    internal static bool IsStructurallyInvalidEntryName(string name)
    {
        if (string.IsNullOrEmpty(name))
            return true;

        // Exact structural records are skipped by the walkers, not rejected.
        if (name is "." or "..")
            return false;

        foreach (char c in name)
        {
            if (c is '/' or '\\')
                return true;
        }

        return false;
    }

    /// <summary>
    /// Returns true when <paramref name="name"/> cannot be used as a host file
    /// name safely. Structural <c>"."</c>/<c>".."</c> records are not flagged
    /// here — walkers skip those separately — but names that normalize to them
    /// on Windows are.
    /// </summary>
    internal static bool IsInvalidEntryName(string name)
    {
        if (IsStructurallyInvalidEntryName(name))
            return true;

        // Exact structural records are skipped by the walkers, not rejected.
        if (name is "." or "..")
            return false;

        foreach (char c in name)
        {
            if (c == ':' && OperatingSystem.IsWindows())
                return true;
        }

        if (OperatingSystem.IsWindows())
        {
            // Win32 strips trailing dots/spaces, so a name that normalizes to
            // nothing, to a parent reference, or to a *different* existing name
            // (`foo.` -> `foo`) would escape or silently collide on extract.
            if (name.TrimEnd(' ', '.').Length != name.Length)
                return true;

            // DOS device names are special even with an extension (`CON.txt`
            // still opens the console). Extracting one would fail or, worse,
            // write to a device instead of a file.
            if (IsReservedWindowsName(name))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Replaces every host-unsafe character with <c>'_'</c>. The replacement is
    /// length-preserving in Latin1, so the repairer can patch the name bytes in
    /// place; on Windows trailing dots/spaces are also replaced because Win32
    /// would silently strip them.
    /// </summary>
    internal static string SanitizeEntryName(string name)
    {
        char[] chars = name.ToCharArray();
        for (int i = 0; i < chars.Length; i++)
        {
            if (IsInvalidEntryChar(chars[i]))
                chars[i] = '_';
        }

        if (OperatingSystem.IsWindows())
        {
            if (IsReservedWindowsName(name))
            {
                chars[0] = '_';
            }

            int end = chars.Length;
            while (end > 0 && (chars[end - 1] == ' ' || chars[end - 1] == '.'))
                end--;

            bool normalizedAway = end == 0 || new string(chars, 0, end) is "." or "..";
            int replaceFrom = normalizedAway ? 0 : end;
            for (int i = replaceFrom; i < chars.Length; i++)
                chars[i] = '_';
        }

        return new string(chars);
    }

    /// <summary>
    /// Returns true when <paramref name="name"/> is a DOS device name on
    /// Windows (case-insensitive, with or without an extension): <c>CON</c>,
    /// <c>PRN</c>, <c>AUX</c>, <c>NUL</c>, <c>CONIN$</c>, <c>CONOUT$</c>,
    /// <c>COM1</c>…<c>COM9</c> and <c>LPT1</c>…<c>LPT9</c>.
    /// </summary>
    internal static bool IsReservedWindowsName(string name)
    {
        if (!OperatingSystem.IsWindows() || string.IsNullOrEmpty(name))
            return false;

        int dot = name.IndexOf('.');
        string stem = (dot >= 0 ? name[..dot] : name).TrimEnd(' ');
        if (stem.Length == 0)
            return false;

        if (stem.Equals("CON", StringComparison.OrdinalIgnoreCase) ||
            stem.Equals("PRN", StringComparison.OrdinalIgnoreCase) ||
            stem.Equals("AUX", StringComparison.OrdinalIgnoreCase) ||
            stem.Equals("NUL", StringComparison.OrdinalIgnoreCase) ||
            stem.Equals("CONIN$", StringComparison.OrdinalIgnoreCase) ||
            stem.Equals("CONOUT$", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (stem.Length == 4 &&
            (stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase) ||
             stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)) &&
            stem[3] is >= '1' and <= '9')
        {
            return true;
        }

        return false;
    }

    // Escape-capable characters only. The colon is drive-relative on Windows
    // (`C:evil` resolves against another directory) but a perfectly valid host
    // character on Unix, where the writer/patcher accept it — rejecting it
    // there would make XISOSharp unable to read images it just created.
    // Other host-invalid characters (NUL, `<`, `>` …) still fail per-file at
    // create time with a named ExtractFileException, which continue-on-error
    // can record.
    private static bool IsInvalidEntryChar(char c) =>
        c is '/' or '\\' || (c == ':' && OperatingSystem.IsWindows());
}
