using Romd.Admin.Application.Ingestion.Jobs;
using Romd.Admin.Application.Titles.Enrichment;
using Romd.Domain.Jobs;
namespace Romd.Infrastructure.Jobs.Executors;

public sealed class EnrichmentJobExecutor(ITitleEnrichmentPipeline pipeline) : IJobExecutor<EnrichmentJob>
{
    public Task ExecuteAsync(EnrichmentJob job, JobContext context) =>
        pipeline.EnrichAsync(job.TitleId, job.PlatformId, context, job.ArtworkOnly);
}
