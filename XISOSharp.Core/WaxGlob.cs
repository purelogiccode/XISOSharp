using System.Text;
using System.Text.RegularExpressions;

namespace XISOSharp;

/// <summary>
/// Wax-compatible glob matcher with capture group support.
/// Mirrors the semantics of the Rust <c>wax</c> crate (0.6.0) as used by
/// <c>xdvdfs-core/src/write/fs/remap.rs</c>: every pattern element forms a capture,
/// the whole match is index 0, subsequent indices are left-to-right pattern captures.
/// </summary>
internal sealed class WaxGlob
{
    private readonly Regex _regex;

    public WaxGlob(string pattern)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        Pattern = pattern;
        var regexStr = BuildRegex(pattern);
        _regex = new Regex(regexStr, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    public string Pattern { get; }

    public bool IsMatch(string candidate) => _regex.IsMatch(candidate);

    /// <summary>
    /// Returns captures where index 0 is the whole match, 1..N are pattern captures.
    /// Returns null when the candidate does not match.
    /// </summary>
    public IReadOnlyList<string>? GetCaptures(string candidate)
    {
        var m = _regex.Match(candidate);
        if (!m.Success) return null;
        var list = new List<string>(m.Groups.Count);
        // Groups[0] is whole match
        for (int i = 0; i < m.Groups.Count; i++)
        {
            var g = m.Groups[i];
            list.Add(g.Success ? g.Value : string.Empty);
        }

        return list;
    }

    public string GetCapture(string candidate, int index)
    {
        var caps = GetCaptures(candidate);
        if (caps == null) return string.Empty;
        if (index < 0 || index >= caps.Count) return string.Empty;
        return caps[index];
    }

    internal string RegexPattern => _regex.ToString();

    // --- regex building ---

    private static string BuildRegex(string pattern)
    {
        // Wax patterns are relative, strip leading slash and dot components.
        // Keep empty pattern as "^$" (matches empty/root).
        if (string.IsNullOrEmpty(pattern))
            return "^$";

        var trimmed = pattern.TrimStart('/');
        // Also trim leading "./"
        while (trimmed.StartsWith("./", StringComparison.Ordinal))
            trimmed = trimmed[2..];
        if (string.Equals(trimmed, ".", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(trimmed, "./", StringComparison.OrdinalIgnoreCase))
            trimmed = string.Empty;

        if (string.IsNullOrEmpty(trimmed))
            return "^$";

        var segments = trimmed.Split('/');
        int count = segments.Length;
        var sb = new StringBuilder("^");

        for (int i = 0; i < count; i++)
        {
            var seg = segments[i];
            if (string.Equals(seg, "**", StringComparison.OrdinalIgnoreCase))
            {
                if (count == 1)
                {
                    sb.Append("(.*)");
                }
                else if (i == 0)
                {
                    sb.Append("((?:[^/]+/)*)");
                }
                else if (i == count - 1)
                {
                    sb.Append("(?:/(.*))?");
                }
                else
                {
                    sb.Append("/((?:[^/]+/)*)");
                }

                continue;
            }

            // Non-** segment
            if (i > 0)
            {
                // Add separator unless previous segment was **
                if (!string.Equals(segments[i - 1], "**", StringComparison.OrdinalIgnoreCase))
                {
                    sb.Append('/');
                }
                else
                {
                    // Previous was **: its regex already handles separator.
                    // For leading ** case, next segment attaches directly (no slash)
                    // For middle ** case, slash was already included in "/((?:...)*)"
                    // For trailing ** there is no next, not reached.
                }
            }

            // Validate that segment does not contain embedded "**"
            if (seg.Contains("**", StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    $"Invalid glob pattern '{pattern}': tree wildcard '**' must be alone as a path component.");
            }

            var segRegex = FragmentToRegex(seg, capturing: true);
            sb.Append(segRegex);
        }

        sb.Append('$');
        return sb.ToString();
    }

    /// <summary>
    /// Converts a single path segment fragment (no '/' separators) to regex.
    /// When <paramref name="capturing"/> is false, wildcards/alternatives produce
    /// non-capturing groups so they don't contribute to the capture index sequence.
    /// </summary>
    private static string FragmentToRegex(string fragment, bool capturing)
    {
        var sb = new StringBuilder();
        int i = 0;
        while (i < fragment.Length)
        {
            char c = fragment[i];
            switch (c)
            {
                case '*':
                    if (capturing) sb.Append("([^/]*)");
                    else sb.Append("(?:[^/]*)");
                    i++;
                    break;
                case '$':
                    // wax lazy star – treat same as eager for matching, still capturing
                    if (capturing) sb.Append("([^/]*)");
                    else sb.Append("(?:[^/]*)");
                    i++;
                    break;
                case '?':
                    if (capturing) sb.Append("([^/])");
                    else sb.Append("(?:[^/])");
                    i++;
                    break;
                case '[':
                    if (TryParseCharClass(fragment, i, out var cc, out var end))
                    {
                        sb.Append(cc);
                        i = end;
                    }
                    else
                    {
                        sb.Append(Regex.Escape("["));
                        i++;
                    }

                    break;
                case '{':
                    {
                        int braceEnd = FindMatchingBrace(fragment, i);
                        if (braceEnd == -1)
                            throw new ArgumentException($"Unclosed '{{' in glob fragment '{fragment}'");
                        string inner = fragment.Substring(i + 1, braceEnd - i - 1);
                        var opts = SplitAlternatives(inner);
                        var optRegexes = new List<string>(opts.Count);
                        foreach (var opt in opts)
                        {
                            // Inside alternative, inner patterns are non-capturing per wax spec
                            var optRegex = FragmentToRegex(opt, capturing: false);
                            optRegexes.Add(optRegex);
                        }

                        string combined = string.Join("|", optRegexes);
                        if (capturing)
                        {
                            sb.Append('(');
                            sb.Append("(?:");
                            sb.Append(combined);
                            sb.Append("))");
                        }
                        else
                        {
                            sb.Append("(?:");
                            sb.Append(combined);
                            sb.Append(')');
                        }

                        i = braceEnd + 1;
                    }
                    break;
                case '<':
                    {
                        int angleEnd = fragment.IndexOf('>', i);
                        if (angleEnd == -1)
                        {
                            sb.Append(Regex.Escape("<"));
                            i++;
                            break;
                        }

                        string repContent = fragment.Substring(i + 1, angleEnd - i - 1);
                        int colon = repContent.IndexOf(':');
                        string subGlob = colon >= 0 ? repContent.Substring(0, colon) : repContent;
                        string bound = colon >= 0 ? repContent.Substring(colon + 1) : string.Empty;
                        string subRegex = FragmentToRegex(subGlob, capturing: false);
                        string quant = BoundToQuantifier(bound, colon >= 0);
                        if (capturing)
                        {
                            sb.Append('(');
                            sb.Append("(?:");
                            sb.Append(subRegex);
                            sb.Append(')');
                            sb.Append(quant);
                            sb.Append(')');
                        }
                        else
                        {
                            sb.Append("(?:");
                            sb.Append(subRegex);
                            sb.Append(quant);
                            sb.Append(')');
                        }

                        i = angleEnd + 1;
                    }
                    break;
                case '\\' when i + 1 < fragment.Length:
                    sb.Append(Regex.Escape(fragment[i + 1].ToString()));
                    i += 2;
                    break;
                default:
                    sb.Append(Regex.Escape(c.ToString()));
                    i++;
                    break;
            }
        }

        return sb.ToString();
    }

    private static string BoundToQuantifier(string bound, bool hasColon)
    {
        if (!hasColon)
        {
            // <a>  -> zero or more  (spec: omission of colon -> zero or more)
            return "*";
        }

        if (string.IsNullOrEmpty(bound))
        {
            // <a:> -> one or more
            return "+";
        }

        // bound may be "0," , "1,4" , "3" , etc.
        var parts = bound.Split(',', 2);
        string lowerStr = parts[0].Trim();
        string upperStr = parts.Length > 1 ? parts[1].Trim() : null!;
        bool hasLower = !string.IsNullOrEmpty(lowerStr);
        bool hasUpper = parts.Length > 1 && !string.IsNullOrEmpty(upperStr);
        bool hasComma = parts.Length > 1;

        if (!hasLower && !hasUpper && !hasComma)
        {
            // single value without comma? e.g., "3"
            if (int.TryParse(lowerStr, System.Globalization.CultureInfo.InvariantCulture, out var v)) return $"{{{v}}}";
            return "+";
        }

        if (hasLower && hasComma && !hasUpper)
        {
            if (int.TryParse(lowerStr, System.Globalization.CultureInfo.InvariantCulture, out var lower))
                return lower == 0 ? "*" : $"{{{lower},}}";
            return "*";
        }

        if (hasLower && hasUpper)
        {
            if (int.TryParse(lowerStr, System.Globalization.CultureInfo.InvariantCulture, out var lower) &&
                int.TryParse(upperStr, System.Globalization.CultureInfo.InvariantCulture, out var upper))
                return $"{{{lower},{upper}}}";
            return "+";
        }

        if (!hasLower && hasUpper)
        {
            if (int.TryParse(upperStr, System.Globalization.CultureInfo.InvariantCulture, out var upper))
                return $"{{0,{upper}}}";
            return "*";
        }

        if (hasLower && !hasComma)
        {
            if (int.TryParse(lowerStr, System.Globalization.CultureInfo.InvariantCulture, out var v)) return $"{{{v}}}";
        }

        return "+";
    }

    private static int FindMatchingBrace(string s, int start)
    {
        int depth = 0;
        for (int i = start; i < s.Length; i++)
        {
            char c = s[i];
            if (c == '\\' && i + 1 < s.Length)
            {
                i++; // skip escaped
                continue;
            }

            if (c == '{')
            {
                depth++;
            }
            else if (c == '}')
            {
                depth--;
                if (depth == 0) return i;
            }
        }

        return -1;
    }

    private static List<string> SplitAlternatives(string inner)
    {
        var result = new List<string>();
        var sb = new StringBuilder();
        int depth = 0;
        for (int i = 0; i < inner.Length; i++)
        {
            char c = inner[i];
            if (c == '\\' && i + 1 < inner.Length)
            {
                sb.Append(c);
                sb.Append(inner[i + 1]);
                i++;
                continue;
            }

            if (c == '{')
            {
                depth++;
            }
            else if (c == '}')
            {
                depth--;
            }
            else if (c == ',' && depth == 0)
            {
                result.Add(sb.ToString());
                sb.Clear();
                continue;
            }

            sb.Append(c);
        }

        result.Add(sb.ToString());
        return result;
    }

    private static bool TryParseCharClass(string glob, int start, out string charClass, out int end)
    {
        int i = start + 1;
        bool negate = false;
        if (i < glob.Length && (glob[i] == '!' || glob[i] == '^'))
        {
            negate = true;
            i++;
        }

        var content = new StringBuilder();
        bool closed = false;
        while (i < glob.Length)
        {
            char c = glob[i];
            if (c == ']' && content.Length > 0)
            {
                closed = true;
                i++;
                break;
            }

            if (c == '\\' && i + 1 < glob.Length)
            {
                content.Append(Regex.Escape(glob[i + 1].ToString()));
                i += 2;
                continue;
            }

            if (c == '[') break;
            content.Append(c == '-' ? "-" : Regex.Escape(c.ToString()));
            i++;
        }

        if (!closed || content.Length == 0)
        {
            charClass = string.Empty;
            end = start;
            return false;
        }

        charClass = "[" + (negate ? "^" : string.Empty) + content + "]";
        try
        {
            _ = new Regex(charClass);
        }
        catch (ArgumentException)
        {
            charClass = string.Empty;
            end = start;
            return false;
        }

        end = i;
        return true;
    }
}