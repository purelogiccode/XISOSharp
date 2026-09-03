using System.IO;
using System.Security.Cryptography;

namespace XISOSharpTester.Services;

/// <summary>
/// Provides static utility methods for computing cryptographic
/// hashes (SHA-256 and MD5) of files and converting hash bytes
/// to hexadecimal strings.
/// </summary>
public static class HashUtil
{
    /// <summary>
    /// Converts a byte array to its lowercase hexadecimal string
    /// representation.
    /// </summary>
    /// <param name="a">The byte array to convert. If <c>null</c>, returns "(none)".</param>
    /// <returns>The lowercase hex string, or "(none)" if the input is <c>null</c>.</returns>
    public static string ToHex(byte[]? a)
    {
        if (a == null) return "(none)";

        return Convert.ToHexString(a).ToLowerInvariant();
    }

    /// <summary>
    /// Determines whether every byte in the array is zero.
    /// </summary>
    /// <param name="a">The byte array to check.</param>
    /// <returns><c>true</c> if all bytes are zero; otherwise <c>false</c>.</returns>
    public static bool IsAllZero(byte[] a)
    {
        foreach (var b in a)
        {
            if (b != 0)
                return false;
        }

        return true;
    }

    /// <summary>
    /// Computes the SHA-256 hash of the file at the specified path
    /// and returns it as a lowercase hexadecimal string.
    /// </summary>
    /// <param name="filePath">The path to the file to hash.</param>
    /// <returns>The SHA-256 hash as a lowercase hex string.</returns>
    public static string ComputeSha256(string filePath)
    {
        using var sha = SHA256.Create();
        using var fs = File.OpenRead(filePath);
        var hash = sha.ComputeHash(fs);
        return ToHex(hash);
    }

    /// <summary>
    /// Computes the MD5 hash of the file at the specified path
    /// and returns it as a lowercase hexadecimal string.
    /// </summary>
    /// <param name="filePath">The path to the file to hash.</param>
    /// <returns>The MD5 hash as a lowercase hex string.</returns>
    public static string ComputeMd5(string filePath)
    {
        using var md5 = MD5.Create();
        using var fs = File.OpenRead(filePath);
        var hash = md5.ComputeHash(fs);
        return ToHex(hash);
    }
}