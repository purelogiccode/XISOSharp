using System.Security.Cryptography;

namespace XISOSharp;

/// <summary>
/// Full logical-image hashes. Computed over the decompressed byte stream, so
/// an ISO, CISO, CCI or split ISO of the same image produces identical values.
/// </summary>
/// <param name="TotalBytes">Number of bytes hashed (the logical image length).</param>
/// <param name="Crc32">Lowercase CRC32 hex.</param>
/// <param name="Md5">Lowercase MD5 hex.</param>
/// <param name="Sha1">Lowercase SHA-1 hex.</param>
/// <param name="Sha256">Lowercase SHA-256 hex.</param>
/// <param name="Sha3256">Lowercase SHA3-256 hex.</param>
public sealed record XisoImageHashResult(
    long TotalBytes,
    string Crc32,
    string Md5,
    string Sha1,
    string Sha256,
    string Sha3256);

/// <summary>
/// Streaming full-image hashes for archival verification (Redump-style
/// CRC32/MD5/SHA-1 plus modern SHA-256 and SHA3-256). The image is read through
/// <see cref="XisoImageSource.OpenRead"/>, so compressed and split inputs hash
/// exactly like the plain ISO they decode to.
/// </summary>
public static class XisoImageHash
{
    private static readonly uint[] Crc32Table = BuildCrc32Table();

    /// <summary>
    /// Computes CRC32, MD5, SHA-1, SHA-256 and SHA3-256 in one streaming pass.
    /// </summary>
    /// <param name="imagePath">Plain ISO, split ISO, CISO or CCI image.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The five digests and the number of bytes hashed.</returns>
    /// <exception cref="ArgumentException">The path is empty.</exception>
    /// <exception cref="FileNotFoundException">The image does not exist.</exception>
    /// <exception cref="NotSupportedException">The path is a ZAR archive.</exception>
    /// <exception cref="IOException">The image cannot be read.</exception>
    public static XisoImageHashResult Compute(string imagePath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(imagePath);
        cancellationToken.ThrowIfCancellationRequested();

        if (XisoImageSource.TryDetect(imagePath, out XisoImageSourceInfo? source) && source is not null &&
            source.Format == XisoImageFormat.Zar)
        {
            throw new NotSupportedException(
                "ZArchive (.zar) files are not sector images; use XisoZarchive.ComputeZarChecksum for a tree hash.");
        }

        using Stream stream = XisoImageSource.OpenRead(imagePath);
        return Compute(stream, cancellationToken);
    }

    /// <summary>
    /// Computes the five hashes over an already-open logical image stream. The
    /// stream must be readable and seekable; it is left open.
    /// </summary>
    /// <param name="imageStream">Logical image stream (plain or decompressed).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The five digests and the number of bytes hashed.</returns>
    public static XisoImageHashResult Compute(Stream imageStream, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(imageStream);
        if (!imageStream.CanRead)
        {
            throw new ArgumentException("Image stream must be readable.", nameof(imageStream));
        }

        imageStream.Seek(0, SeekOrigin.Begin);

        using IncrementalHash md5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
        using IncrementalHash sha1 = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        using IncrementalHash sha256 = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        bool nativeSha3 = SHA3_256.IsSupported;
        using IncrementalHash? nativeSha3Hasher = nativeSha3
            ? IncrementalHash.CreateHash(HashAlgorithmName.SHA3_256)
            : null;
        using Sha3256? managedSha3 = nativeSha3 ? null : new Sha3256();

        uint crc = 0xFFFFFFFFu;
        long total = 0;
        byte[] buffer = new byte[1 << 20];
        int read;
        while ((read = imageStream.Read(buffer, 0, buffer.Length)) > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ReadOnlySpan<byte> chunk = buffer.AsSpan(0, read);
            md5.AppendData(chunk);
            sha1.AppendData(chunk);
            sha256.AppendData(chunk);
            if (nativeSha3Hasher is not null)
            {
                nativeSha3Hasher.AppendData(chunk);
            }
            else
            {
                managedSha3!.AppendData(chunk);
            }

            for (int i = 0; i < chunk.Length; i++)
            {
                crc = (crc >> 8) ^ Crc32Table[(crc ^ chunk[i]) & 0xFF];
            }

            total += read;
        }

        byte[] sha3Digest = nativeSha3Hasher is not null
            ? nativeSha3Hasher.GetHashAndReset()
            : managedSha3!.GetHashAndReset();

        return new XisoImageHashResult(
            total,
            (~crc).ToString("x8", System.Globalization.CultureInfo.InvariantCulture),
            Convert.ToHexString(md5.GetHashAndReset()).ToLowerInvariant(),
            Convert.ToHexString(sha1.GetHashAndReset()).ToLowerInvariant(),
            Convert.ToHexString(sha256.GetHashAndReset()).ToLowerInvariant(),
            Convert.ToHexString(sha3Digest).ToLowerInvariant());
    }

    /// <summary>Asynchronous wrapper over <see cref="Compute(string, CancellationToken)"/>.</summary>
    /// <param name="imagePath">Image path.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public static Task<XisoImageHashResult> ComputeAsync(string imagePath,
        CancellationToken cancellationToken = default) =>
        Task.Run(() => Compute(imagePath, cancellationToken), cancellationToken);

    private static uint[] BuildCrc32Table()
    {
        uint[] table = new uint[256];
        for (uint i = 0; i < table.Length; i++)
        {
            uint value = i;
            for (int bit = 0; bit < 8; bit++)
            {
                value = (value & 1) != 0 ? 0xEDB88320u ^ (value >> 1) : value >> 1;
            }

            table[i] = value;
        }

        return table;
    }
}
