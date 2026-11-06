namespace Romd.Admin.Application.Titles.Enrichment;

/// <summary>
///     Records the outcome of a single provider's enrichment attempt.
/// </summary>
public sealed record ProviderAttempt
{
    public required string ProviderId { get; init; }
    public required bool Succeeded { get; init; }
    public string? Error { get; init; }
    public float? MatchConfidence { get; init; }
    public required TimeSpan Duration { get; init; }
}
