using ErrorOr;
using Romd.Admin.Application.Ingestion.Jobs;
using Microsoft.Extensions.Options;
using Romd.Admin.Application.Configuration;
using Romd.Application.Common.Cqrs;
using Romd.Contracts.Management.Enrichment;

namespace Romd.Admin.Application.Source.Platform.Queries.GetMetadataPolicy;

public sealed record GetMetadataPolicyQuery(int PlatformId) : IQuery<PlatformMetadataPolicy>;

public sealed class GetMetadataPolicyQueryHandler(
    IPlatformRepository platforms,
    IPlatformFieldDefaultRepository defaults,
    IMetadataRematerializationJobRepository jobs,
    IOptions<EnrichmentOptions> options) : IQueryHandler<GetMetadataPolicyQuery, PlatformMetadataPolicy>
{
    public async Task<ErrorOr<PlatformMetadataPolicy>> HandleAsync(GetMetadataPolicyQuery query, CancellationToken ct = default)
    {
        if (await platforms.GetByIdAsync(query.PlatformId, ct) is null)
            return Error.NotFound("Platform.NotFound", "System not found.");
        var saved = await defaults.GetByPlatformIdAsync(query.PlatformId, ct);
        return new PlatformMetadataPolicy(MetadataPolicy.Revision(saved, options.Value.GlobalSourcePriority), saved,
            options.Value.GlobalSourcePriority, await defaults.CountTitlesAsync(query.PlatformId, ct),
            await jobs.GetPendingIdAsync(query.PlatformId, ct));
    }
}
