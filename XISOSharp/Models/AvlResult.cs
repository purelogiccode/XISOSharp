namespace XISOSharp.Models;

/// <summary>Result codes returned by AVL tree insertion.</summary>
public enum AvlResult
{
    /// <summary>Operation completed successfully without requiring rebalancing.</summary>
    NoErr,

    /// <summary>An error occurred during the operation.</summary>
    AvlError,

    /// <summary>Operation completed and the tree was rebalanced.</summary>
    AvlBalanced
}
