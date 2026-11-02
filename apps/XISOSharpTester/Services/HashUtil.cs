using System.Security.Cryptography;
using Serilog;

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
    /// Computes the SHA-256 hash of the file at the specified path
    /// and returns it as a lowercase hexadecimal string.
    /// </summary>
    /// <param name="filePath">The path to the file to hash.</param>
    /// <returns>The SHA-256 hash as a lowercase hex string.</returns>
    public static string ComputeSha256(string filePath)
    {
        try
        {
            ArgumentException.ThrowIfNullOrEmpty(filePath);
            using SHA256 sha = SHA256.Create();
            using FileStream fs = File.OpenRead(filePath);
            byte[] hash = sha.ComputeHash(fs);
            return ToHex(hash);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "ComputeSha256 failed for {Path}", filePath);
            throw;
        }
    }
}
