namespace Romd.Admin.Application.Titles.Enrichment;

/// <summary>
///     Accumulates every provider's outcome for one title. Providers never short-circuit each other:
///     a title is enriched when any accepted result carried usable data, failed only when every provider
///     that answered errored, and otherwise not found.
/// </summary>
internal sealed class TitleEnrichmentTally
{
    private int _completed;
    private int _errors;

    public bool Enriched { get; private set; }
    public float BestConfidence { get; private set; }

    /// <summary>A provider answered without an error but contributed no usable data.</summary>
    public void RecordCompleted() => _completed++;

    /// <summary>A provider errored, threw, or returned a result that failed identity acceptance.</summary>
    public void RecordError() => _errors++;

    /// <summary>An accepted result; its usable data and confidence count toward the title's outcome.</summary>
    public void RecordAccepted(float confidence, bool hasUsableData)
    {
        if (hasUsableData)
            Enriched = true;
        if (confidence > BestConfidence)
            BestConfidence = confidence;
    }

    /// <summary>The effective metadata is rematerialized once, after all providers, and only when trusted enough.</summary>
    public bool ShouldRematerialize(float minimumConfidence) => Enriched && BestConfidence >= minimumConfidence;

    public OrchestrationResult ToResult() =>
        Enriched ? OrchestrationResult.Found(BestConfidence)
        : _completed == 0 && _errors > 0 ? OrchestrationResult.Failed
        : OrchestrationResult.NotFound;
}
