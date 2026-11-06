namespace Romd.Admin.Application.Titles.ReadModels;

/// <summary>
///     Statistics about enrichment status across titles.
/// </summary>
public sealed record TitleEnrichmentStats(
    int Pending,
    int Completed,
    int NotFound,
    int Failed,
    int LowConfidence);
