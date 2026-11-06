using ErrorOr;
using Microsoft.Extensions.Logging;
using Romd.Admin.Application.Common.Persistence;
using Romd.Admin.Application.MetadataProviders;
using Romd.Application.Common.Concurrency;
using Romd.Application.Common.Cqrs;
using Romd.Contracts.Management.Artwork;

namespace Romd.Admin.Application.Artwork.Commands.ReplaceArtworkEnrichmentSettings;

public sealed record ReplaceArtworkEnrichmentSettingsCommand(UpdateArtworkEnrichmentSettingsRequest Settings, RevisionPrecondition IfMatch)
    : ICommand<ArtworkEnrichmentSettingsDto>;

public sealed class ReplaceArtworkEnrichmentSettingsCommandHandler(IArtworkAcquisitionStore store, IEnumerable<IProviderIdentityAdapter> adapters,
    IUnitOfWork unitOfWork, ILogger<ReplaceArtworkEnrichmentSettingsCommandHandler> logger)
    : ICommandHandler<ReplaceArtworkEnrichmentSettingsCommand, ArtworkEnrichmentSettingsDto>
{
    public async Task<ErrorOr<ArtworkEnrichmentSettingsDto>> HandleAsync(ReplaceArtworkEnrichmentSettingsCommand command, CancellationToken ct = default)
    {
        var providers = new List<string>();
        foreach (var adapter in adapters.Where(adapter => adapter.Capabilities.Contains("artwork")))
        {
            var state = await adapter.GetAvailabilityAsync(ct);
            if (state.Enabled && state.Configured)
                providers.Add(adapter.Id);
        }
        return await unitOfWork.ExecuteInTransactionAsync<ArtworkEnrichmentSettingsDto>(async token =>
        {
            var accepted = await store.StageSettingsAsync(command.Settings, command.IfMatch, token);
            return accepted.IsError ? accepted.Errors : accepted.Value with { Providers = providers };
        }, logger, ct);
    }
}
