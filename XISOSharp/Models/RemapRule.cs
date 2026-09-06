using System.Text;

namespace XISOSharp.Models;

/// <summary>
/// Represents a host-to-image path mapping rule for <c>build-image</c>.
/// Mirrors <c>RemapOverlayConfig.map_rules</c> in <c>xdvdfs-core/src/write/fs/remap.rs</c>.
/// </summary>
public sealed class RemapRule
{
    /// <summary>Host glob pattern (without leading '!').</summary>
    public string HostGlob { get; set; } = string.Empty;

    /// <summary>Image rewrite path (may contain <c>{0}</c>, <c>{1}</c> captures).</summary>
    public string ImagePath { get; set; } = string.Empty;

    /// <summary>When <c>true</c>, this rule is an exclusion (host starts with '!').</summary>
    public bool IsExclusion { get; set; }

    /// <summary>
    /// Tries to parse a remap rule string of the form <c>hostGlob[:imagePath]</c>.
    /// </summary>
    /// <remarks>
    /// The separator is the first <c>:</c> that is neither backslash-escaped nor a
    /// Windows drive-letter colon (<c>X:</c> or <c>!X:</c> followed by <c>/</c> or
    /// <c>\</c>). Inside either part, <c>\:</c> is a literal colon and <c>\\</c> a
    /// literal backslash; a backslash before any other character stays literal so
    /// Windows paths keep working. The TOML spec form needs no escaping (values
    /// are already delimited by TOML quoting).
    /// </remarks>
    /// <param name="raw">Raw rule text to parse.</param>
    /// <param name="rule">Parsed rule on success; otherwise <c>null</c>.</param>
    /// <param name="error">Error message on failure; otherwise <c>null</c>.</param>
    /// <returns><c>true</c> if parsing succeeded; otherwise <c>false</c>.</returns>
    public static bool TryParse(string raw, out RemapRule? rule, out string? error)
    {
        rule = null;
        error = null;
        if (string.IsNullOrWhiteSpace(raw))
        {
            error = "Map rule cannot be empty";
            return false;
        }

        // Split on the first ':' that is neither escaped nor a drive-letter colon
        // (upstream xdvdfs splits on every ':' and drops the extras; the TOML
        // spec form is unaffected — it never passes through this parser).
        var colon = FindSeparator(raw);
        string host;
        string image;
        if (colon >= 0)
        {
            host = raw.Substring(0, colon);
            image = raw.Substring(colon + 1);
        }
        else
        {
            host = raw;
            image = string.Empty;
        }

        host = UnescapePart(host.Trim());
        image = UnescapePart(image.Trim());

        if (string.IsNullOrEmpty(host))
        {
            error = $"Map rule \"{raw}\" has empty host pattern";
            return false;
        }

        var isExclusion = host.StartsWith('!');
        if (!isExclusion && string.IsNullOrEmpty(image))
        {
            error = $"Map rule \"{host}\" must have an image path unless it is an exclusion rule (starting with '!')";
            return false;
        }

        // Validate host glob can be built (strip !)
        var hostForGlob = isExclusion ? host.Substring(1) : host;
        if (string.IsNullOrEmpty(hostForGlob))
        {
            error = $"Exclusion rule \"{host}\" has empty host pattern after '!'";
            return false;
        }

        try
        {
            _ = new WaxGlob(hostForGlob);
        }
        catch (Exception ex)
        {
            error = $"Invalid host glob \"{hostForGlob}\": {ex.Message}";
            return false;
        }

        // Validate image rewrite substitutions
        try
        {
            FindMatchIndices(image);
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }

        rule = new RemapRule { HostGlob = hostForGlob, ImagePath = image, IsExclusion = isExclusion };
        // Store original host with '!'? Keep without '!' but IsExclusion flag indicates.
        // For serialization we need to know original host string including '!'? HostGlob stripped is fine.
        // Keep original host for dry-run display? We'll reconstruct as needed.
        return true;
    }

    internal string HostWithBang => IsExclusion ? "!" + HostGlob : HostGlob;

    /// <summary>
    /// Finds the host/image separator: the first <c>:</c> that is not escaped
    /// with a backslash and is not a Windows drive-letter colon, or -1.
    /// </summary>
    private static int FindSeparator(string raw)
    {
        for (var i = 0; i < raw.Length; i++)
        {
            if (raw[i] == '\\' && i + 1 < raw.Length &&
                (raw[i + 1] == ':' || raw[i + 1] == '\\'))
            {
                i++; // skip the escaped character
                continue;
            }

            if (raw[i] == ':' && !IsDriveColon(raw, i))
                return i;
        }

        return -1;
    }

    /// <summary>
    /// Reports whether the colon at <paramref name="colon"/> is a Windows
    /// drive-letter colon: <c>X:</c> (or <c>!X:</c> for exclusions) at the start
    /// of the rule, followed by <c>/</c> or <c>\</c>.
    /// </summary>
    private static bool IsDriveColon(string raw, int colon)
    {
        var letter = colon - 1;
        if (letter < 0 || !char.IsAsciiLetter(raw[letter]))
            return false;
        if (letter != 0 && !(letter == 1 && raw[0] == '!'))
            return false;
        return colon + 1 < raw.Length &&
            (raw[colon + 1] == '/' || raw[colon + 1] == '\\');
    }

    /// <summary>
    /// Resolves <c>\:</c> to <c>:</c> and <c>\\</c> to <c>\</c>; any other
    /// backslash is kept literally so Windows paths survive untouched.
    /// </summary>
    private static string UnescapePart(string part)
    {
        if (part.IndexOf('\\') < 0)
            return part;
        var sb = new StringBuilder(part.Length);
        for (var i = 0; i < part.Length; i++)
        {
            if (part[i] == '\\' && i + 1 < part.Length &&
                (part[i + 1] == ':' || part[i + 1] == '\\'))
            {
                sb.Append(part[i + 1]);
                i++;
            }
            else
            {
                sb.Append(part[i]);
            }
        }

        return sb.ToString();
    }

    internal static List<int> FindMatchIndices(string rewrite)
    {
        var indices = new List<int>();
        var matching = false;
        var current = 0;
        for (var idx = 0; idx < rewrite.Length; idx++)
        {
            var c = rewrite[idx];
            if (c == '{')
            {
                if (matching)
                    throw new ArgumentException($"Invalid rewrite substitution \"{rewrite}\" (at {idx}): nested '{{'");
                matching = true;
                current = 0;
                continue;
            }

            if (!matching) continue;
            if (c == '}')
            {
                matching = false;
                indices.Add(current);
                current = 0;
                continue;
            }

            if (c >= '0' && c <= '9')
            {
                current = (current * 10) + (c - '0');
                continue;
            }

            throw new ArgumentException(
                $"Invalid rewrite substitution \"{rewrite}\" (at {idx}): expected digit character");
        }

        if (matching)
        {
            throw new ArgumentException(
                $"Invalid rewrite substitution \"{rewrite}\" (at {rewrite.Length - 1}): unclosed brace");
        }

        return indices;
    }
}