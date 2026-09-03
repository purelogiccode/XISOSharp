using XISOSharp;

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

        // Split on first ':'
        var colon = raw.IndexOf(':');
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

        host = host.Trim();
        image = image.Trim();

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