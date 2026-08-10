using System.Text;
using System.Text.RegularExpressions;

namespace XISOSharp;

/// <summary>
/// Matches relative file paths against shell-style glob patterns, used to exclude
/// files and directories when creating XISO images.
/// </summary>
/// <remarks>
/// <para>Supported syntax (use <c>/</c> as the path separator):</para>
/// <list type="bullet">
/// <item><c>*</c> — matches any sequence of characters within a single path segment.</item>
/// <item><c>?</c> — matches exactly one character within a single path segment.</item>
/// <item><c>**</c> — as a complete segment, matches zero or more path segments.
/// A trailing <c>/**</c> also matches the directory itself, so <c>build/**</c> excludes
/// the <c>build</c> directory and everything below it.</item>
/// <item><c>[abc]</c>, <c>[a-z]</c>, <c>[!abc]</c> — character classes with optional
/// <c>!</c>/<c>^</c> negation.</item>
/// <item><c>\x</c> — escapes the next character so it is matched literally.</item>
/// </list>
/// <para>
/// A pattern without a leading <c>**/</c> is anchored to the source root. Matching is
/// case-insensitive. A trailing <c>/</c> is treated as <c>/**</c> (the directory and
/// everything below it).
/// </para>
/// </remarks>
public sealed class GlobMatcher
{
    private readonly Regex[] _patterns;

    /// <summary>
    /// Initializes a new matcher from the given glob patterns.
    /// Empty or <c>null</c> patterns are ignored. Each pattern is compiled once at
    /// construction time.
    /// </summary>
    /// <param name="patterns">Glob patterns to match against. Must not be <c>null</c>.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="patterns"/> is <c>null</c>.</exception>
    public GlobMatcher(IEnumerable<string> patterns)
    {
        ArgumentNullException.ThrowIfNull(patterns);

        _patterns = patterns
            .Where(static p => !string.IsNullOrEmpty(p))
            .Select(static p => new Regex(
                GlobToRegex(p),
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking))
            .ToArray();
    }

    /// <summary>
    /// Returns <c>true</c> when the given relative path matches any of the configured
    /// glob patterns.
    /// </summary>
    /// <param name="relativePath">
    /// Path relative to the source root, using <c>/</c> separators (e.g. <c>"sub/file.txt"</c>).
    /// Backslashes are normalized to forward slashes. A <c>null</c> or empty path matches nothing.
    /// </param>
    /// <returns><c>true</c> when at least one pattern matches, otherwise <c>false</c>.</returns>
    public bool IsMatch(string? relativePath)
    {
        if (string.IsNullOrEmpty(relativePath))
            return false;

        var path = relativePath.Replace('\\', '/');
        foreach (var pattern in _patterns)
        {
            if (pattern.IsMatch(path))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Converts a glob pattern to its equivalent regular expression.
    /// </summary>
    /// <param name="glob">The glob pattern to convert.</param>
    /// <returns>An anchored regular expression matching the same set of relative paths.</returns>
    internal static string GlobToRegex(string glob)
    {
        if (string.IsNullOrEmpty(glob))
            return "^$";

        // A trailing slash means "the directory and everything below it", unless the
        // pattern already ends with a '**' segment (e.g. "a/**/" behaves like "a/**").
        string normalized = glob;
        if (glob.EndsWith('/'))
        {
            var trimmed = glob.TrimEnd('/');
            normalized = trimmed.EndsWith("/**", StringComparison.Ordinal) || string.Equals(trimmed, "**", StringComparison.Ordinal)
                ? trimmed
                : trimmed + "/**";
        }

        var segments = normalized.Split('/');
        var sb = new StringBuilder("^");
        var count = segments.Length;

        for (var i = 0; i < count; i++)
        {
            var segment = segments[i];
            if (string.Equals(segment, "**", StringComparison.Ordinal))
            {
                if (count == 1)
                {
                    sb.Append(".*");
                }
                else if (i == 0)
                {
                    sb.Append("(?:[^/]+/)*");
                }
                else if (i == count - 1)
                {
                    sb.Append("(?:/.*)?");
                }
                else
                {
                    if (i > 0 && !string.Equals(segments[i - 1], "**", StringComparison.Ordinal))
                    {
                        sb.Append('/');
                    }

                    sb.Append("(?:[^/]+/)*");
                }
            }
            else
            {
                if (i > 0 && !string.Equals(segments[i - 1], "**", StringComparison.Ordinal))
                {
                    sb.Append('/');
                }

                AppendSegmentRegex(sb, segment);
            }
        }

        sb.Append('$');
        return sb.ToString();
    }

    private static void AppendSegmentRegex(StringBuilder sb, string segment)
    {
        var i = 0;
        while (i < segment.Length)
        {
            var c = segment[i];
            switch (c)
            {
                case '*':
                    sb.Append("[^/]*");
                    i++;
                    break;
                case '?':
                    sb.Append("[^/]");
                    i++;
                    break;
                case '[' when TryParseCharClass(segment, i, out var charClass, out var end):
                    sb.Append(charClass);
                    i = end;
                    break;
                case '\\' when i + 1 < segment.Length:
                    sb.Append(Regex.Escape(segment[i + 1].ToString()));
                    i += 2;
                    break;
                default:
                    sb.Append(Regex.Escape(c.ToString()));
                    i++;
                    break;
            }
        }
    }

    /// <summary>
    /// Parses a <c>[...]</c> character class starting at <paramref name="start"/>.
    /// Returns <c>false</c> (and leaves the output empty) when the class is malformed,
    /// in which case the caller treats the <c>[</c> as a literal character.
    /// </summary>
    private static bool TryParseCharClass(string glob, int start, out string charClass, out int end)
    {
        var i = start + 1;
        var negate = false;
        if (i < glob.Length && (glob[i] == '!' || glob[i] == '^'))
        {
            negate = true;
            i++;
        }

        var content = new StringBuilder();
        var closed = false;
        while (i < glob.Length)
        {
            var c = glob[i];
            if (c == ']' && content.Length > 0)
            {
                closed = true;
                i++;
                break;
            }

            if (c == '\\' && i + 1 < glob.Length)
            {
                // The escaped character becomes a literal (regex-escaped) class member.
                content.Append(Regex.Escape(glob[i + 1].ToString()));
                i += 2;
                continue;
            }

            if (c == '[')
            {
                // Nested classes are not supported; bail out so '[' is treated literally.
                break;
            }

            // '-' stays raw so ranges like [a-z] keep working; everything else is escaped.
            content.Append(c == '-' ? "-" : Regex.Escape(c.ToString()));
            i++;
        }

        if (!closed || content.Length == 0)
        {
            charClass = "";
            end = start;
            return false;
        }

        charClass = "[" + (negate ? "^" : "") + content + "]";

        // Reject classes that are invalid in .NET regex (e.g. descending ranges like
        // [z-a] or [\z-a]) so a malformed pattern degrades to a literal '[' instead of
        // throwing from the matcher constructor.
        try
        {
            _ = new Regex(charClass, RegexOptions.NonBacktracking);
        }
        catch (ArgumentException)
        {
            charClass = "";
            end = start;
            return false;
        }

        end = i;
        return true;
    }
}
