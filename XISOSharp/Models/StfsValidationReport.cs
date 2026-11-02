namespace XISOSharp.Models;

/// <summary>
/// Result of a deep integrity check of a single-file Xbox 360 STFS package:
/// per-block SHA-1 hashes, the level-0/level-1/level-2 hash tables, the top
/// hash in the volume descriptor and the header hash.
/// </summary>
/// <param name="IsValid">Whether every checked hash and block chain matched.</param>
/// <param name="FilesChecked">Number of file entries whose block chains were walked.</param>
/// <param name="BlocksChecked">Number of logical data blocks whose SHA-1 hash was verified.</param>
/// <param name="HashTablesChecked">Number of hash tables (level 0 and level 1) whose SHA-1 hash was verified.</param>
/// <param name="BytesChecked">Number of data-block bytes hashed.</param>
/// <param name="Issues">Every mismatch or structural problem found; empty when <paramref name="IsValid"/> is <c>true</c>.</param>
public sealed record StfsValidationReport(
    bool IsValid,
    int FilesChecked,
    int BlocksChecked,
    int HashTablesChecked,
    long BytesChecked,
    IReadOnlyList<StfsValidationIssue> Issues);
