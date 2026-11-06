using Romd.Admin.Application.Ingestion.Jobs;
using Romd.Domain.Catalog;
using Romd.Domain.Source.Platform;

namespace Romd.Admin.Application.Artwork;

public interface IAutomaticArtworkService
{
    Task FillAsync(Title title, Platform platform, JobContext context, CancellationToken ct,
        IReadOnlyList<Romd.Admin.Application.Titles.Enrichment.ProviderArtworkResult>? results = null);
}

public sealed record EnrichmentArtworkCandidate(string AssetId, ArtworkRole Role, string Url, int Width, int Height);
