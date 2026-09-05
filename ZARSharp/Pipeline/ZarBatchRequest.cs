namespace ZARSharp.Pipeline;

/// <summary>
/// One batch request, bundling what <c>ZarManagerCore.__init__</c> takes per
/// run (items, target, workers, mode, keep-originals, policy). Ports the
/// <c>ProcessRequest</c> model (<c>models/process.py</c>, ZarManager 1.2.0).
/// </summary>
public sealed record ZarBatchRequest(
    IReadOnlyList<string> Items,
    string TargetDirectory,
    ZarProcessMode Mode = ZarProcessMode.Auto,
    bool KeepOriginals = true,
    ZarCollisionPolicy Policy = ZarCollisionPolicy.Fail,
    int MaxWorkers = 4)
{
    /// <summary>Expands to <see cref="ZarPipelineOptions"/>.</summary>
    public ZarPipelineOptions ToPipelineOptions() => new()
    {
        MaxDegreeOfParallelism = MaxWorkers,
        CollisionPolicy = Policy,
        DeleteSourceOnSuccess = !KeepOriginals,
    };
}
