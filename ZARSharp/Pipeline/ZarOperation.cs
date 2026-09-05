namespace ZARSharp.Pipeline;

/// <summary>
/// Operation a <see cref="ZarProgress"/> event belongs to.
/// </summary>
public enum ZarOperation
{
    /// <summary>Packing entries into a <c>.zar</c> archive.</summary>
    Pack = 0,

    /// <summary>Extracting a <c>.zar</c> archive to a directory.</summary>
    Extract = 1,
}
