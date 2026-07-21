using System.IO;
using System.Security.Cryptography;

namespace ExtractXisoTester.Services;

public static class HashUtil
{
    public static string ToHex(byte[]? a)
    {
        if (a == null) return "(none)";
        return Convert.ToHexString(a).ToLowerInvariant();
    }

    public static bool IsAllZero(byte[] a)
    {
        foreach (var b in a) if (b != 0) return false;
        return true;
    }

    public static string ComputeSha256(string filePath)
    {
        using var sha = SHA256.Create();
        using var fs = File.OpenRead(filePath);
        var hash = sha.ComputeHash(fs);
        return ToHex(hash);
    }

    public static string ComputeMd5(string filePath)
    {
        using var md5 = MD5.Create();
        using var fs = File.OpenRead(filePath);
        var hash = md5.ComputeHash(fs);
        return ToHex(hash);
    }
}
