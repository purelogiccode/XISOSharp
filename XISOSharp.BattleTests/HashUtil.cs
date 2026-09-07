using System.Security.Cryptography;

namespace XISOSharp.BattleTests;

/// <summary>SHA-256 / SHA-512 helpers for file and directory hashing.</summary>
internal static class HashUtil
{
    /// <summary>Computes hex SHA-256 of a file.</summary>
    public static string ComputeSha256(string path)
    {
        using SHA256 sha = SHA256.Create();
        using FileStream fs = new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536);
        byte[] hash = sha.ComputeHash(fs);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>Computes hex SHA-256 of a byte array.</summary>
    public static string ComputeSha256(byte[] data)
    {
        byte[] hash = SHA256.HashData(data);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>Computes hex SHA-256 of all files under a directory (sorted, relative paths included).</summary>
    public static IReadOnlyDictionary<string, string> HashDirectory(string root)
    {
        // BTL-008: ordinal (case-sensitive) keys — on Linux `A` vs `a` are
        // distinct files; collapsing them hid files (false pass). Callers
        // report case-only differences as mismatches.
        SortedDictionary<string, string> dict = new(StringComparer.Ordinal);
        foreach (string file in Directory.GetFiles(root, "*", SearchOption.AllDirectories))
        {
            string rel = Path.GetRelativePath(root, file);
            dict[rel] = ComputeSha256(file);
        }

        return dict;
    }
}
