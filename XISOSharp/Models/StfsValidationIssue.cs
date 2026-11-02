namespace XISOSharp.Models;

/// <summary>
/// One problem found while validating a single-file Xbox 360 STFS package.
/// </summary>
/// <param name="Area">
/// What the issue belongs to: a package path, a logical block
/// (<c>block 123</c>), a hash table (<c>hash table level 0 group 3</c>), the
/// top hash (<c>top hash</c>) or the header hash (<c>header hash</c>).
/// </param>
/// <param name="Reason">Human-readable description of the mismatch or structural problem.</param>
public sealed record StfsValidationIssue(string Area, string Reason);
