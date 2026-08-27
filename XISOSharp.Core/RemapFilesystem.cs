using System.Text;

using XISOSharp.DataStructures;

namespace XISOSharp;

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
        int colon = raw.IndexOf(':');
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

        bool isExclusion = host.StartsWith("!", StringComparison.Ordinal);
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
        bool matching = false;
        int current = 0;
        for (int idx = 0; idx < rewrite.Length; idx++)
        {
            char c = rewrite[idx];
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
                current = current * 10 + (c - '0');
                continue;
            }

            throw new ArgumentException(
                $"Invalid rewrite substitution \"{rewrite}\" (at {idx}): expected digit character");
        }

        if (matching)
            throw new ArgumentException(
                $"Invalid rewrite substitution \"{rewrite}\" (at {rewrite.Length - 1}): unclosed brace");
        return indices;
    }
}

/// <summary>
/// Implements ordered path remapping (xdvdfs <c>build-image</c> parity) using wax-compatible globs
/// and <c>RemapOverlayFilesystem</c> semantics from <c>xdvdfs-core/src/write/fs/remap.rs</c>.
/// </summary>
public static class RemapFilesystem
{
    private sealed class FileEntry
    {
        public string Name = string.Empty;
        public bool IsDirectory;
        public long Length;
    }

    /// <summary>
    /// Parses an <c>xdvdfs.toml</c> spec file. Returns output path (if present) and ordered map rules.
    /// </summary>
    public static (string? output, List<RemapRule> rules) ParseSpecFile(string specPath)
    {
        if (!File.Exists(specPath))
            throw new FileNotFoundException($"Spec file not found: {specPath}", specPath);
        var text = File.ReadAllText(specPath, Encoding.UTF8);
        return ParseSpecText(text);
    }

    /// <summary>
    /// Parses <c>xdvdfs.toml</c> spec text into an output path and ordered remap rules.
    /// </summary>
    /// <param name="toml">TOML text containing <c>[metadata]</c> and <c>[map_rules]</c> sections.</param>
    /// <returns>Tuple of optional output path and list of remap rules.</returns>
    public static (string? output, List<RemapRule> rules) ParseSpecText(string toml)
    {
        string? output = null;
        var rules = new List<RemapRule>();
        string currentSection = string.Empty;
        foreach (var rawLine in toml.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#') || line.StartsWith(';'))
                continue;
            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                currentSection = line.Substring(1, line.Length - 2).Trim();
                continue;
            }

            int eq = line.IndexOf('=');
            if (eq < 0) continue;
            var keyPart = line.Substring(0, eq).Trim();
            var valPart = line.Substring(eq + 1).Trim();

            // Strip inline comments not inside quotes? Simple: ignore after # if not in quotes.
            // For simplicity, assume no inline comments.

            string key = UnquoteTomlKey(keyPart);
            string val = UnquoteTomlValue(valPart);

            if (string.Equals(currentSection, "metadata", StringComparison.OrdinalIgnoreCase))
            {
                if (string.Equals(key, "output", StringComparison.OrdinalIgnoreCase))
                    output = val;
            }
            else if (string.Equals(currentSection, "map_rules", StringComparison.OrdinalIgnoreCase))
            {
                // key is host glob, val is image path
                bool isExcl = key.StartsWith("!", StringComparison.Ordinal);
                var hostForGlob = isExcl ? key.Substring(1) : key;
                // Validate
                try
                {
                    _ = new WaxGlob(hostForGlob);
                }
                catch { continue; }

                var rr = new RemapRule { HostGlob = hostForGlob, ImagePath = val, IsExclusion = isExcl };
                rules.Add(rr);
            }
        }

        return (output, rules);
    }

    private static string UnquoteTomlKey(string s)
    {
        s = s.Trim();
        if (s.Length >= 2 && ((s[0] == '"' && s[^1] == '"') || (s[0] == '\'' && s[^1] == '\'')))
        {
            var inner = s.Substring(1, s.Length - 2);
            return inner.Replace("\\\"", "\"").Replace(@"\\", "\\");
        }

        return s;
    }

    private static string UnquoteTomlValue(string s)
    {
        s = s.Trim();
        // Remove trailing comment outside quotes? ignore.
        if (s.Length >= 2 && s[0] == '"' && s[^1] == '"')
        {
            var inner = s.Substring(1, s.Length - 2);
            return inner.Replace("\\\"", "\"").Replace(@"\\", "\\").Replace("\\n", "\n").Replace("\\t", "\t");
        }

        if (s.Length >= 2 && s[0] == '\'' && s[^1] == '\'')
        {
            return s.Substring(1, s.Length - 2);
        }

        // Bare value (unlikely for our spec) – return as is without quotes
        // Strip possible trailing comment
        int comment = s.IndexOf('#');
        if (comment >= 0) s = s.Substring(0, comment).Trim();
        return s;
    }

    /// <summary>
    /// Generates <c>xdvdfs.toml</c> spec text from remap rules and an optional output path.
    /// </summary>
    /// <param name="rules">Ordered remap rules to serialize.</param>
    /// <param name="output">Optional output ISO path for the <c>[metadata]</c> section.</param>
    /// <returns>TOML text representing the spec.</returns>
    public static string GenerateSpecText(IEnumerable<RemapRule> rules, string? output)
    {
        var sb = new StringBuilder();
        if (!string.IsNullOrEmpty(output))
        {
            sb.AppendLine("[metadata]");
            sb.AppendLine($"output = \"{EscapeTomlString(output)}\"");
            sb.AppendLine();
        }

        sb.AppendLine("[map_rules]");
        foreach (var r in rules)
        {
            var host = r.HostWithBang;
            var img = r.ImagePath;
            sb.AppendLine($"\"{EscapeTomlString(host)}\" = \"{EscapeTomlString(img)}\"");
        }

        return sb.ToString();
    }

    private static string EscapeTomlString(string s) => s.Replace("\\", @"\\").Replace("\"", "\\\"");

    /// <summary>
    /// Dry-run: returns ordered host→image mappings without building an image.
    /// Paths are returned with leading '/'.
    /// </summary>
    public static IReadOnlyList<(string HostPath, string ImagePath)> DryRunRemap(string sourceDir,
        IReadOnlyList<RemapRule> rules)
    {
        ArgumentException.ThrowIfNullOrEmpty(sourceDir);
        ArgumentNullException.ThrowIfNull(rules);
        if (!Directory.Exists(sourceDir))
            throw new DirectoryNotFoundException($"Source directory not found: {sourceDir}");

        var result = new List<(string, string)>();
        foreach ((string hostRel, string guestRel) in BuildMappings(sourceDir, rules))
        {
            var host = "/" + hostRel;
            var guest = "/" + guestRel;
            if (string.Equals(guest, "/", StringComparison.OrdinalIgnoreCase))
            {
                /* root maps – guest "/" is fine */
            }

            // guestRel may be empty for root file? But empty means "/" already.
            // For file mapped to root file "default.xbe", guestRel = "default.xbe" => "/default.xbe"
            result.Add((host, guest));
        }

        return result;
    }

    /// <summary>
    /// Builds an XISO image with ordered remapping.
    /// </summary>
    public static int BuildImage(string sourceDir, string outputIsoPath, IReadOnlyList<RemapRule> rules,
        IProgress<ProgressInfo>? progress = null, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(sourceDir);
        ArgumentException.ThrowIfNullOrEmpty(outputIsoPath);
        ArgumentNullException.ThrowIfNull(rules);
        if (!Directory.Exists(sourceDir))
        {
            Logger.LogErr($"Source directory not found: {sourceDir}\n");
            return 1;
        }

        if (rules.Count == 0)
        {
            Logger.LogErr("Must specify at least one map rule\n");
            return 1;
        }

        ct.ThrowIfCancellationRequested();

        // Build mappings and AVL tree
        AvlNode? avlRoot;
        try
        {
            avlRoot = BuildAvlTree(sourceDir, rules, ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Logger.LogErr($"Failed to build remap filesystem: {ex.Message}\n");
            return 1;
        }

        // Delegate to XisoWriter remap writer (handles isRemap flag)
        try
        {
            return XisoWriter.CreateFromRemapTree(avlRoot, outputIsoPath, progress: progress, cancellationToken: ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Logger.LogErr($"Failed to create image: {ex.Message}\n");
            return 1;
        }
    }

    // --- internal helpers ---

    private static List<(string hostRel, string guestRel)> BuildMappings(string sourceDir,
        IReadOnlyList<RemapRule> rules)
    {
        // Prepare WaxGlobs for host patterns and all_globs check
        var waxGlobs = new List<WaxGlob>(rules.Count);
        foreach (var r in rules)
        {
            waxGlobs.Add(new WaxGlob(r.HostGlob));
        }

        // Walk host filesystem gathering matches
        var matches = new List<(string path, FileEntry entry, string prefix)>();
        var dirStack = new Stack<(string dirRel, string? parentPrefix)>();
        dirStack.Push((string.Empty, null));

        while (dirStack.Count > 0)
        {
            (string dirRel, string? parentPrefix) = dirStack.Pop();
            string fullDir = string.IsNullOrEmpty(dirRel)
                ? sourceDir
                : Path.Combine(sourceDir, dirRel.Replace('/', Path.DirectorySeparatorChar));

            string[] entries;
            try
            {
                entries = Directory.GetFileSystemEntries(fullDir);
            }
            catch
            {
                continue;
            }

            foreach (var fullEntry in entries)
            {
                string name = Path.GetFileName(fullEntry);
                if (name is "." or "..") continue;
                string entryRel = string.IsNullOrEmpty(dirRel) ? name : dirRel + "/" + name;

                bool isDir;
                long len = 0;
                try
                {
                    var attr = File.GetAttributes(fullEntry);
                    isDir = (attr & FileAttributes.Directory) != FileAttributes.None;
                    if (!isDir)
                    {
                        len = new FileInfo(fullEntry).Length;
                    }
                }
                catch { continue; }

                var fe = new FileEntry { Name = name, IsDirectory = isDir, Length = len };

                bool directMatch = false;
                foreach (var g in waxGlobs)
                {
                    if (g.IsMatch(entryRel))
                    {
                        directMatch = true;
                        break;
                    }
                }

                string? matchPrefix;
                if (directMatch) matchPrefix = entryRel;
                else if (parentPrefix != null) matchPrefix = parentPrefix;
                else matchPrefix = null;

                if (isDir)
                {
                    dirStack.Push((entryRel, matchPrefix));
                }

                if (matchPrefix != null)
                {
                    matches.Add((entryRel, fe, matchPrefix));
                }
            }
        }

        // For each match, compute rewritten guest path via ordered rules
        var result = new List<(string hostRel, string guestRel)>();
        // To preserve first-wins for duplicate guest paths, we need to track guest->host first occurrence.
        // But BuildMappings for DryRun should list all host->guest pairs that survive after remap logic,
        // including duplicates where first wins – later duplicates should be omitted from result? In xdvdfs dump,
        // they iterate over trie which already deduplicates (first wins). For dry-run list we should mimic deduplicated output.
        var guestSeen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach ((string path, FileEntry entry, string prefix) in matches)
        {
            string? rewritten = null;
            // Iterate rules in order
            for (int idx = 0; idx < rules.Count; idx++)
            {
                var rule = rules[idx];
                var glob = waxGlobs[idx];
                var caps = glob.GetCaptures(prefix);
                if (caps == null) continue;

                if (rule.IsExclusion)
                {
                    rewritten = null;
                    continue;
                }

                if (rewritten != null) continue;

                string rewrite = rule.ImagePath;
                // Validate and substitute captures
                var indices = RemapRule.FindMatchIndices(rewrite);
                foreach (var mi in indices.Distinct())
                {
                    string repl = mi < caps.Count ? caps[mi] : string.Empty;
                    rewrite = rewrite.Replace("{" + mi + "}", repl, StringComparison.Ordinal);
                }

                // Suffix handling
                string suffix = string.Empty;
                if (!string.Equals(path, prefix, StringComparison.OrdinalIgnoreCase))
                {
                    // path is descendant of prefix – suffix is remainder including leading slash
                    if (path.StartsWith(prefix + "/", StringComparison.OrdinalIgnoreCase))
                        suffix = path.Substring(prefix.Length); // includes leading '/'
                    else if (prefix.Length == 0)
                        suffix = "/" + path;
                    else
                        suffix = string.Empty; // should not happen
                }

                if (!string.IsNullOrEmpty(suffix))
                {
                    rewrite = rewrite.TrimEnd('/') + suffix;
                }

                // Normalize to PathVec components
                var normalized = NormalizeImagePath(rewrite);
                rewritten = normalized;
            }

            if (rewritten != null)
            {
                // Normalize guest path for dedup: compare case-insensitive, keep first
                string guestKey = rewritten; // already normalized without leading slash
                // Also need to consider that rewritten may be empty (root). For a directory entry whose rewritten is empty ("/"), guestKey = "" (root). Should we add mapping for that directory itself? In dump they add only non-prefix-directory entries. For path that maps to root, the host directory itself maps to root, but we typically don't list that as a file mapping; its children will be listed. However if host is a file that maps to root file, guestKey will be file name, not empty.
                // For DryRun we should include only entries where the mapping corresponds to a file or a directory that is leaf? But original dump includes both files and non-empty directories that are mapped (non-prefix). Our result currently includes both files and directories that survived. Should we filter to only leaf non-prefix? For dry-run, original Rust dumps only entries where !is_prefix_directory (leaf) or directories that are host directories (isDir). But our simplified approach includes every path/prefix that got a rewritten – that includes intermediate files and directories. Should we include directories? In BuildImage test, they expect to list files under dest, not directories themselves? Let's include files and directories but keep deduplication similar to trie: first-wins for same guest path.

                // Guest path empty corresponds to root – don't list root itself as a file entry
                if (guestKey.Length == 0 && entry.IsDirectory)
                {
                    // Directory mapped to root – no separate entry needed; its children will be separate.
                    continue;
                }

                if (guestSeen.Add(guestKey))
                {
                    result.Add((path, guestKey));
                }
            }
        }

        return result;
    }

    private static string NormalizeImagePath(string rewrite)
    {
        if (string.IsNullOrEmpty(rewrite)) return string.Empty;
        // Trim leading '.' and '/'
        var t = rewrite.TrimStart('.', '/');
        // Split and filter empty components (handles "//" etc)
        var parts = t.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return string.Empty;
        return string.Join("/", parts);
    }

    private static AvlNode BuildAvlTree(string sourceDir, IReadOnlyList<RemapRule> rules, CancellationToken ct)
    {
        // Build AVL tree incrementally
        var dirCache = new Dictionary<string, AvlNode>(StringComparer.OrdinalIgnoreCase);
        var imageRoot = new AvlNode
        {
            Filename = "IMAGE", StartSector = Constants.RootDirectorySector, Subdirectory = null
        };
        dirCache[string.Empty] = imageRoot;

        // First ensure all directory paths needed for file mappings exist
        // For each mapping, we need to handle host file vs directory
        // mappings list is (hostRel, guestRel) where guestRel is normalized image path (without leading slash)
        // hostRel is host relative path
        // We need to know if host entry is file or directory to decide node type.
        // Our BuildMappings currently discarded entry type; we need to preserve it.
        // To fix, we should rebuild mappings with entry type.
        // Instead, recompute with type info: reuse BuildMappings but need entry IsDirectory.
        // Let's re-walk with type-aware collection.

        // Rebuild mapping with types (avoid double walk by recomputing here)
        // For simplicity, call a variant that returns types.
        var typedMappings = BuildTypedMappings(sourceDir, rules);

        foreach ((string hostRel, string guestRel, bool isDir) in typedMappings)
        {
            ct.ThrowIfCancellationRequested();
            if (isDir)
            {
                // Guest is a directory path
                if (string.IsNullOrEmpty(guestRel))
                    continue; // root
                EnsureDir(guestRel);
                // If directory is empty, its Subdirectory should be EmptySentinel. We'll finalize later.
            }
            else
            {
                // File
                string parentPath = GetParentPath(guestRel);
                string fileName = GetFileName(guestRel);
                if (string.IsNullOrEmpty(fileName))
                    continue; // should not happen (file at root with empty name)

                var parentNode = EnsureDir(parentPath);
                // Check duplicate file in same directory (first wins)
                if (AvlTree.AvlFetch(parentNode.Subdirectory, fileName) != null)
                    continue;

                string hostFull = Path.Combine(sourceDir, hostRel.Replace('/', Path.DirectorySeparatorChar));
                var fi = new FileInfo(hostFull);
                if (!fi.Exists)
                    continue;
                if (fi.Length > uint.MaxValue)
                    throw new XisoFileTooLargeException(fileName, fi.Length);

                var fileNode = new AvlNode
                {
                    Filename = fileName, FileSize = (uint)fi.Length, Subdirectory = null, HostPath = fi.FullName
                };
                AvlNode? tmp = parentNode.Subdirectory;
                AvlTree.AvlInsert(ref tmp, fileNode);
                parentNode.Subdirectory = tmp;
                // If duplicate due to case-insensitive, we already checked, but insert may still fail if race.
            }
        }

        // Finalize empty directories: any directory node whose Subdirectory is still null should be EmptySentinel
        foreach (var kv in dirCache)
        {
            var node = kv.Value;
            if (node.Subdirectory == null)
            {
                // If node is not the imageRoot and has no children, mark empty
                // For imageRoot itself, if no children, its Subdirectory stays null -> caller will handle as empty
                // But we want empty directories to be EmptySubdirectory, not null, so writer knows to emit empty sector.
                // Determine if this node corresponds to a directory that should be empty vs has children but not yet set?
                // If node has children in dirCache (i.e., any child path starts with dirPath + "/"), then it should have Subdirectory non-null (already set via insertions). If not, it's empty.
                bool hasChild = false;
                if (!string.IsNullOrEmpty(kv.Key))
                {
                    string prefix = kv.Key + "/";
                    foreach (var otherKey in dirCache.Keys)
                    {
                        if (otherKey.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                        {
                            hasChild = true;
                            break;
                        }
                    }
                }
                else
                {
                    // root – check if it has any child already (Subdirectory already may have files)
                    hasChild = node.Subdirectory != null;
                    // Also check if any file was inserted directly under root (already reflected in Subdirectory)
                    // If root has no Subdirectory at all, it's empty root – leave as Empty?
                    // For empty root, caller expects EmptySubdirectory sentinel.
                }

                if (!hasChild)
                {
                    node.Subdirectory = AvlNode.EmptySubdirectory;
                }
            }
        }

        var rootChildren = imageRoot.Subdirectory;
        // If root has empty sentinel, keep it; if null and no mappings, set to Empty
        if (rootChildren == null)
            rootChildren = AvlNode.EmptySubdirectory;

        return rootChildren;

        // Helper to ensure directory node exists for a given dir path (relative image path, no leading slash)
        AvlNode EnsureDir(string dirPath)
        {
            if (dirCache.TryGetValue(dirPath, out var existing))
                return existing;
            // Find parent
            string parentPath = GetParentPath(dirPath);
            var parentNode = EnsureDir(parentPath);
            string name = GetFileName(dirPath);
            // Check if already exists in parent's AVL
            var found = AvlTree.AvlFetch(parentNode.Subdirectory, name);
            if (found != null)
            {
                dirCache[dirPath] = found;
                return found;
            }

            var newNode = new AvlNode { Filename = name, Subdirectory = null, FileSize = 0 };
            // Insert into parent
            AvlNode? tmp = parentNode.Subdirectory;
            var res = AvlTree.AvlInsert(ref tmp, newNode);
            parentNode.Subdirectory = tmp;
            if (res == AvlResult.AvlError)
            {
                // Duplicate (case-insensitive) – fetch existing
                var dup = AvlTree.AvlFetch(parentNode.Subdirectory, name);
                if (dup != null)
                {
                    dirCache[dirPath] = dup;
                    return dup;
                }
            }

            dirCache[dirPath] = newNode;
            return newNode;
        }
    }

    private static List<(string hostRel, string guestRel, bool isDir)> BuildTypedMappings(string sourceDir,
        IReadOnlyList<RemapRule> rules)
    {
        var waxGlobs = new List<WaxGlob>(rules.Count);
        foreach (var r in rules) waxGlobs.Add(new WaxGlob(r.HostGlob));

        var matches = new List<(string path, FileEntry entry, string prefix)>();
        var dirStack = new Stack<(string dirRel, string? parentPrefix)>();
        dirStack.Push((string.Empty, null));
        while (dirStack.Count > 0)
        {
            (string dirRel, string? parentPrefix) = dirStack.Pop();
            string fullDir = string.IsNullOrEmpty(dirRel)
                ? sourceDir
                : Path.Combine(sourceDir, dirRel.Replace('/', Path.DirectorySeparatorChar));
            string[] entries;
            try { entries = Directory.GetFileSystemEntries(fullDir); }
            catch { continue; }

            foreach (var fullEntry in entries)
            {
                string name = Path.GetFileName(fullEntry);
                if (name is "." or "..") continue;
                string entryRel = string.IsNullOrEmpty(dirRel) ? name : dirRel + "/" + name;
                bool isDir;
                long len = 0;
                try
                {
                    var attr = File.GetAttributes(fullEntry);
                    isDir = (attr & FileAttributes.Directory) != FileAttributes.None;
                    if (!isDir) len = new FileInfo(fullEntry).Length;
                }
                catch { continue; }

                var fe = new FileEntry { Name = name, IsDirectory = isDir, Length = len };
                bool directMatch = false;
                foreach (var g in waxGlobs)
                    if (g.IsMatch(entryRel))
                    {
                        directMatch = true;
                        break;
                    }

                string? matchPrefix = directMatch ? entryRel : parentPrefix;
                if (isDir) dirStack.Push((entryRel, matchPrefix));
                if (matchPrefix != null) matches.Add((entryRel, fe, matchPrefix));
            }
        }

        var result = new List<(string hostRel, string guestRel, bool isDir)>();
        var guestSeen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach ((string path, FileEntry entry, string prefix) in matches)
        {
            string? rewritten = null;
            for (int idx = 0; idx < rules.Count; idx++)
            {
                var rule = rules[idx];
                var glob = waxGlobs[idx];
                var caps = glob.GetCaptures(prefix);
                if (caps == null) continue;
                if (rule.IsExclusion)
                {
                    rewritten = null;
                    continue;
                }

                if (rewritten != null) continue;
                string rewrite = rule.ImagePath;
                var indices = RemapRule.FindMatchIndices(rewrite);
                foreach (var mi in indices.Distinct())
                {
                    string repl = mi < caps.Count ? caps[mi] : string.Empty;
                    rewrite = rewrite.Replace("{" + mi + "}", repl, StringComparison.Ordinal);
                }

                string suffix = string.Empty;
                if (!string.Equals(path, prefix, StringComparison.OrdinalIgnoreCase))
                {
                    if (path.StartsWith(prefix + "/", StringComparison.OrdinalIgnoreCase))
                        suffix = path.Substring(prefix.Length);
                    else if (prefix.Length == 0)
                        suffix = "/" + path;
                }

                if (!string.IsNullOrEmpty(suffix))
                    rewrite = rewrite.TrimEnd('/') + suffix;
                var normalized = NormalizeImagePath(rewrite);
                rewritten = normalized;
            }

            if (rewritten != null)
            {
                if (rewritten.Length == 0 && entry.IsDirectory) continue;
                if (guestSeen.Add(rewritten))
                    result.Add((path, rewritten, entry.IsDirectory));
            }
        }

        return result;
    }

    private static string GetParentPath(string path)
    {
        if (string.IsNullOrEmpty(path)) return string.Empty;
        int slash = path.LastIndexOf('/');
        if (slash < 0) return string.Empty;
        return path.Substring(0, slash);
    }

    private static string GetFileName(string path)
    {
        if (string.IsNullOrEmpty(path)) return string.Empty;
        int slash = path.LastIndexOf('/');
        if (slash < 0) return path;
        return path.Substring(slash + 1);
    }
}