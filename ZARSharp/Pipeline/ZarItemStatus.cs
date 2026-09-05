namespace ZARSharp.Pipeline;

/// <summary>Per-item outcome of a batch run.</summary>
public enum ZarItemStatus
{
    /// <summary>Item finished and its output is complete.</summary>
    Completed = 0,

    /// <summary>Item failed; <see cref="ZarItemResult.ErrorMessage"/> says why.</summary>
    Failed = 1,

    /// <summary>Item skipped (collision policy, or user decline).</summary>
    Skipped = 2,

    /// <summary>Item did not finish due to cancellation.</summary>
    Cancelled = 3,
}
