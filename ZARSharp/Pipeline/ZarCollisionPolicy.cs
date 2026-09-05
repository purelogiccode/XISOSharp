namespace ZARSharp.Pipeline;

/// <summary>
/// What to do when a pipeline output path already exists. Ports
/// <c>CollisionPolicy</c> (<c>models/process.py</c>, ZarManager 1.2.0):
/// <c>SKIP</c> / <c>OVERWRITE</c> / <c>AUTO-RENAME</c> (<c>{stem}_{n}{suffix}</c>).
/// <see cref="Fail"/> additionally preserves the <c>zarchive.exe</c> contract,
/// which refuses to overwrite an existing output file (exit <c>-11</c>).
/// </summary>
public enum ZarCollisionPolicy
{
    /// <summary>Throw <see cref="IOException"/> when the output exists (default).</summary>
    Fail = 0,

    /// <summary>Skip the item, reporting <see cref="ZarItemStatus.Skipped"/>.</summary>
    Skip = 1,

    /// <summary>Delete the existing output before writing.</summary>
    Overwrite = 2,

    /// <summary>Write to <c>{stem}_{n}{suffix}</c>, first free <c>n</c> from 1.</summary>
    AutoRename = 3,
}
