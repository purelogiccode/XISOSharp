using System.Security.Cryptography;

namespace XISOSharp.BattleTests;

/// <summary>SHA-256 / SHA-512 helpers for file and directory hashing.</summary>
internal static class HashUtil
{
    /// <summary>Computes hex SHA-256 of a file.</summary>
    public static string ComputeSha256(string path)
    {
        using var sha = SHA256.Create();
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536);
        var hash = sha.ComputeHash(fs);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>Computes hex SHA-256 of a byte array.</summary>
    public static string ComputeSha256(byte[] data)
    {
        var hash = SHA256.HashData(data);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>Computes hex SHA-256 of all files under a directory (sorted, relative paths included).</summary>
    public static IReadOnlyDictionary<string, string> HashDirectory(string root)
    {
        var dict = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in Directory.GetFiles(root, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(root, file);
            dict[rel] = ComputeSha256(file);
        }
        return dict;
    }
}
