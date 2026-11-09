using System.Text.Json;
using Romd.Admin.Application.Search;
using Romd.Application.Common.Ids;
using Romd.Contracts.Management.Artwork;

namespace Romd.Admin.Application.Artwork;

/// <summary>Existing scope vocabulary and byte-compatible accepted-request identity.</summary>
public static class ArtworkCompletionIntent
{
    public static bool IsValid(ArtworkCompletionRequest request) => request.Mode is ("Artwork" or "Enrich") && (request.Scope switch
    {
        "AllTracked" => true,
        "Automatic" => request.Mode == "Enrich" && (request.Filters is null || ValidFilters(request.Filters)),
        "Selected" => request.TitleIds is { Count: > 0 and <= 10000 } && request.TitleIds.All(x => Sqid.TryParse(x, null, out _)),
        "Filtered" => request.Filters is { } filters && ValidFilters(filters),
        "Retry" => request.RetryJobId is not null && request.RetryStatus is "Failed" or "Unfinished",
        _ => false
    });

    public static ArtworkCompletionRequest Normalize(ArtworkCompletionRequest request) => request with
    {
        TitleIds = request.Scope == "Selected" && request.TitleIds is { } ids
            ? Array.AsReadOnly(ids.Distinct().Order(StringComparer.Ordinal).ToArray()) : null,
        Filters = request.Scope is "Filtered" or "Automatic" && request.Filters is { } filters
            ? filters with { Enrichment = filters.Enrichment is { } flags ? Array.AsReadOnly(flags.ToArray()) : null } : null,
        RetryJobId = request.Scope == "Retry" ? request.RetryJobId : null,
        RetryStatus = request.Scope == "Retry" ? request.RetryStatus : null
    };

    public static string Signature(ArtworkCompletionRequest normalized) =>
        JsonSerializer.Serialize(normalized with { RequestId = Guid.Empty });

    private static bool ValidFilters(ArtworkCompletionFilter filters) =>
        filters.ArtworkStatus is (null or "Poster" or "Backdrop" or "Logo" or "Hero" or "NeedsMatch") &&
        (string.IsNullOrWhiteSpace(filters.EnrichmentStatus) || EnrichmentFilterParser.IsLastAttemptStatus(filters.EnrichmentStatus)) &&
        (filters.Enrichment is null || !EnrichmentFilterParser.ParseFlags(filters.Enrichment).IsError);
}
