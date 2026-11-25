namespace Romd.Admin.Application.MetadataProviders;

public sealed record ProviderGame(
    string Id,
    string Name,
    string Url,
    string? ThumbnailUrl = null,
    int? Year = null,
    IReadOnlyList<string>? Platforms = null,
    IReadOnlyList<string>? Aliases = null,
    IReadOnlyList<ProviderRelease>? Releases = null);

public sealed record ProviderRelease(int? Year, string? Platform);

/// <summary>Whether this query's observed response was exhausted; never a provider catalog or snapshot guarantee.</summary>
public enum ProviderGameSearchCompleteness { Complete, ProviderLimited, BudgetLimited }

/// <summary>
///     Bounded provider suggestions in live relevance order. Incomplete searches require a narrower query or explicit
///     id/page-URL resolution; their absence of another candidate cannot establish a unique automatic identity.
/// </summary>
public sealed record ProviderGameSearchResult(
    IReadOnlyList<ProviderGame> Items,
    ProviderGameSearchCompleteness Completeness,
    int Limit);
