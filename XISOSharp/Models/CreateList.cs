namespace XISOSharp.Models;

/// <summary>
/// Describes a source directory and optional output name for creating an XISO image.
/// Entries can be chained via <see cref="Next"/> for batch creation.
/// </summary>
public class CreateList
{
    /// <summary>Source directory path whose contents will be packed into the XISO.</summary>
    public string Path { get; set; } = "";

    /// <summary>
    /// Optional output filename or path for the resulting ISO.
    /// When <c>null</c> the directory name is used.
    /// </summary>
    public string? Name { get; set; }

    /// <summary>Next entry in a linked list of creation tasks, or <c>null</c>.</summary>
    public CreateList? Next { get; set; }
}