using System.Diagnostics.CodeAnalysis;

namespace XISOSharp.Models;

/// <summary>Outcome of one entry in a <c>.hash</c> verification run.</summary>
public enum HashVerificationStatus
{
    /// <summary>The expected and actual SHA-1 digests match.</summary>
    Match,

    /// <summary>The file exists in the image but its SHA-1 differs.</summary>
    Mismatch,

    /// <summary>The hash file names a file that is absent from the image.</summary>
    Missing,

    /// <summary>The image contains a file the hash file does not name.</summary>
    Extra,
}

/// <summary>One file compared during <see cref="XISOSharp.XisoSkeleton.VerifyHashes"/>.</summary>
/// <param name="Path">Image-internal path normalized with a leading <c>/</c>.</param>
/// <param name="ExpectedSha1">SHA-1 from the hash file, or <c>null</c> for an extra image file.</param>
/// <param name="ActualSha1">SHA-1 computed from the image, or <c>null</c> when the file is missing.</param>
/// <param name="Status">Comparison outcome.</param>
[SuppressMessage("ReSharper", "NotAccessedPositionalProperty.Global",
    Justification = "Public verification payload: consumers render every field; the library only builds it.")]
public sealed record HashVerificationEntry(
    string Path,
    string? ExpectedSha1,
    string? ActualSha1,
    HashVerificationStatus Status);

/// <summary>Result of verifying an image against a <c>.hash</c> sidecar.</summary>
/// <param name="Checked">Number of hash-file entries checked.</param>
/// <param name="Matched">Entries whose SHA-1 matched.</param>
/// <param name="Mismatched">Entries with a different SHA-1.</param>
/// <param name="Missing">Entries absent from the image.</param>
/// <param name="Extra">Image files not named by the hash file.</param>
/// <param name="Entries">Per-file results, sorted by path.</param>
[SuppressMessage("ReSharper", "NotAccessedPositionalProperty.Global",
    Justification = "Public verification payload: consumers render the per-file entries; the library only builds it.")]
public sealed record HashVerificationResult(
    int Checked,
    int Matched,
    int Mismatched,
    int Missing,
    int Extra,
    IReadOnlyList<HashVerificationEntry> Entries)
{
    /// <summary>Whether every hash-file entry matched and no extra image files exist.</summary>
    public bool Passed => Mismatched == 0 && Missing == 0 && Extra == 0;
}
