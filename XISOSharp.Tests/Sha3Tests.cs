using System.Text;

namespace XISOSharp.Tests;

/// <summary>
/// Unit tests for the managed <see cref="Sha3256"/> fallback: NIST FIPS 202
/// vectors, incremental/chunked equivalence, reset semantics, and disposal.
/// </summary>
public class Sha3Tests
{
    private static byte[] Hash(byte[] data)
    {
        using Sha3256 hasher = new();
        hasher.AppendData(data);
        return hasher.GetHashAndReset();
    }

    private static string Hex(byte[] data) => Convert.ToHexString(data).ToLowerInvariant();

    /// <summary>Verifies the NIST SHA3-256 vector for the empty input.</summary>
    [Fact]
    public void Empty_NistVector()
    {
        Assert.Equal("a7ffc6f8bf1ed76651c14756a061d662f580ff4de43b49fa82d80a4b80f8434a", Hex(Hash([])));
    }

    /// <summary>Verifies the NIST SHA3-256 vector for "abc".</summary>
    [Fact]
    public void Abc_NistVector()
    {
        Assert.Equal("3a985da74fe225b2045c172d6bd390bd855f086e3e9d525b46bfe24511431532",
            Hex(Hash(Encoding.ASCII.GetBytes("abc"))));
    }

    /// <summary>Verifies incremental appends match a single-shot hash.</summary>
    [Fact]
    public void Incremental_MatchesOneShot()
    {
        byte[] data = Encoding.ASCII.GetBytes("the quick brown fox jumps over the lazy dog");
        byte[] oneShot = Hash(data);

        using Sha3256 hasher = new();
        hasher.AppendData(data.AsSpan(0, 10));
        hasher.AppendData(data.AsSpan(10, 15));
        hasher.AppendData(data.AsSpan(25));
        Assert.Equal(oneShot, hasher.GetHashAndReset());
    }

    /// <summary>Verifies multi-block chunking matches a single-shot hash.</summary>
    [Fact]
    public void ChunkedMultiBlock_MatchesOneShot()
    {
        byte[] data = new byte[1000];
        Random random = new(4242);
        random.NextBytes(data);
        byte[] expected = Hash(data);

        using Sha3256 hasher = new();
        for (int i = 0; i < data.Length; i += 7)
        {
            hasher.AppendData(data.AsSpan(i, Math.Min(7, data.Length - i)));
        }

        Assert.Equal(expected, hasher.GetHashAndReset());
    }

    /// <summary>Verifies all append overloads produce the same digest.</summary>
    [Fact]
    public void AppendOverloads_AreEquivalent()
    {
        byte[] data = [1, 2, 3, 4, 5];
        byte[] expected = Hash([2, 3, 4, 1, 4, 5]);

        using Sha3256 segment = new();
        segment.AppendData(data, 1, 3);
        segment.AppendData(data.AsSpan(0, 1));
        segment.AppendData([4, 5]);
        Assert.Equal(expected, segment.GetHashAndReset());
    }

    /// <summary>Verifies <c>GetHashAndReset</c> clears state for reuse.</summary>
    [Fact]
    public void GetHashAndReset_ResetsForReuse()
    {
        using Sha3256 hasher = new();
        hasher.AppendData([1, 2, 3]);
        byte[] first = hasher.GetHashAndReset();
        hasher.AppendData([1, 2, 3]);
        byte[] second = hasher.GetHashAndReset();
        Assert.Equal(first, second);
    }

    /// <summary>Verifies out-of-range segments are rejected.</summary>
    [Fact]
    public void AppendData_InvalidSegment_Throws()
    {
        using Sha3256 hasher = new();
        Assert.Throws<ArgumentOutOfRangeException>(() => hasher.AppendData([1], -1, 1));
        Assert.Throws<ArgumentException>(() => hasher.AppendData([1], 0, 2));
    }

    /// <summary>Verifies a null buffer is rejected.</summary>
    [Fact]
    public void AppendData_NullBuffer_Throws()
    {
        using Sha3256 hasher = new();
        Assert.Throws<ArgumentNullException>(() => hasher.AppendData((byte[])null!));
    }

    /// <summary>Verifies a disposed hasher rejects further use.</summary>
    [Fact]
    public void DisposedHasher_Throws()
    {
        Sha3256 hasher = new();
        hasher.AppendData([1]);
        hasher.Dispose();

        Assert.Throws<ObjectDisposedException>(() => hasher.AppendData([2]));
        Assert.Throws<ObjectDisposedException>(() => hasher.GetHashAndReset());
    }
}
