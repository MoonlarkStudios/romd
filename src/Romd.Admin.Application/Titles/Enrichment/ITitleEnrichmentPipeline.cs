using Romd.Admin.Application.Ingestion.Jobs;
namespace Romd.Admin.Application.Titles.Enrichment;

/// <summary>Shared metadata, artwork, materialization and storage lifecycle for one title.</summary>
public interface ITitleEnrichmentPipeline
{
    Task EnrichAsync(int titleId, int? platformId, JobContext context, bool artworkOnly = false);
}
