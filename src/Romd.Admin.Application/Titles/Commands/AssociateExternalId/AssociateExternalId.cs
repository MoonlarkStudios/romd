using Romd.Application.Common.ReferenceCatalog;
using ErrorOr;
using Romd.Admin.Application.Common.Persistence;
using Microsoft.Extensions.Logging;
using Romd.Admin.Application.Catalog;
using Romd.Application.Common.Cqrs;
using Romd.Admin.Application.Source.Platform;
using Romd.Admin.Application.Titles.Enrichment;
using Romd.Admin.Application.Titles.ReadModels;
using Romd.Contracts.Management.Models;
using Romd.Domain.Catalog;

namespace Romd.Admin.Application.Titles.Commands.AssociateExternalId;

/// <summary>
///     Command to manually associate a title with an external provider ID.
///     This allows users to correct automatic matches and triggers metadata fetch.
/// </summary>
public sealed record AssociateExternalIdCommand(
    int TitleId,
    string Provider,
    string ExternalId
) : ICommand<TitleDetail>;

/// <summary>
///     Handler for <see cref="AssociateExternalIdCommand" />.
///     Associates a title with an external provider ID and fetches metadata.
/// </summary>
public sealed class AssociateExternalIdCommandHandler : ICommandHandler<AssociateExternalIdCommand, TitleDetail>
{
    private readonly IReferenceCatalogService _referenceCatalog;
    private readonly ILogger<AssociateExternalIdCommandHandler> _logger;
    private readonly IPlatformRepository _platformRepository;
    private readonly IEnumerable<IMetadataProvider> _providers;
    private readonly IRematerializationScheduler _rematerializationScheduler;
    private readonly ITitleRepository _titleRepository;
    private readonly IUnitOfWork _unitOfWork;

    public AssociateExternalIdCommandHandler(IReferenceCatalogService referenceCatalog,
        ITitleRepository titleRepository,
        IUnitOfWork unitOfWork,
        IPlatformRepository platformRepository,
        IEnumerable<IMetadataProvider> providers,
        IRematerializationScheduler rematerializationScheduler,
        ILogger<AssociateExternalIdCommandHandler> logger)
    {
        _referenceCatalog = referenceCatalog;
        _titleRepository = titleRepository;
        _unitOfWork = unitOfWork;
        _platformRepository = platformRepository;
        _providers = providers;
        _rematerializationScheduler = rematerializationScheduler;
        _logger = logger;
    }

    public async Task<ErrorOr<TitleDetail>> HandleAsync(AssociateExternalIdCommand command,
        CancellationToken ct = default)
    {
        var systemKeys = await _referenceCatalog.GetSystemKeysAsync(ct);
        // 1. Load title with all collections including metadata layers
        var title = await _titleRepository.GetWithMetadataLayersAsync(command.TitleId, ct);
        if (title is null)
        {
            return CatalogErrors.TitleNotFound(command.TitleId);
        }

        // 2. Find the provider
        var provider = _providers.FirstOrDefault(p =>
            p.ProviderId.Equals(command.Provider, StringComparison.OrdinalIgnoreCase));

        if (provider is null)
        {
            return CatalogErrors.ProviderNotFound(command.Provider);
        }

        await provider.InitializeAsync(ct);

        // 3. Validate provider is configured
        if (!provider.IsConfigured)
        {
            return CatalogErrors.ProviderNotConfigured(command.Provider);
        }

        // 4. Set external ID on the title (manual = confirmed)
        title.SetExternalIdManually(provider.ProviderId, command.ExternalId);

        _logger.LogInformation(
            "Associated title '{TitleName}' (ID: {TitleId}) with {Provider} ID '{ExternalId}'",
            title.Name, title.Id, provider.ProviderId, command.ExternalId);

        // 5. Fetch metadata using EnrichAsync with the existing external ID
        var platform = await _platformRepository.GetByIdAsync(title.PlatformId, ct);
        var context = new EnrichmentContext
        {
            TitleName = title.Name,
            PlatformShortName = platform?.ShortName ?? "unknown",
            ExistingExternalId = command.ExternalId
        };

        var enrichResult = await provider.EnrichSingleAsync(context, ct);

        if (enrichResult.Outcome == EnrichmentOutcome.Found && enrichResult.Data?.HasAnyData() == true)
        {
            // 6. Map EnrichmentData to TitleMetadataPayload
            var payload = MapToPayload(enrichResult.Data);

            // 7. Store provider layer (rematerialization happens after save via service)
            title.StoreProviderLayer(provider.ProviderId, MetadataSourceType.Provider, payload);

            _logger.LogInformation(
                "Fetched and applied metadata from {Provider} for title '{TitleName}'",
                provider.ProviderId, title.Name);
        }
        else
        {
            _logger.LogWarning(
                "No metadata returned from {Provider} for external ID '{ExternalId}'",
                provider.ProviderId, command.ExternalId);
        }

        // 8. User-driven association is always Completed
        title.MarkEnrichmentCompleted();

        // Provider I/O has finished; persist the edit and its work intent atomically.
        return await _unitOfWork.ExecuteInTransactionAsync<TitleDetail>(async token =>
        {
            await _rematerializationScheduler.EnqueueTitleAsync(title.Id, token);
            await _titleRepository.UpdateAsync(title, token);
            var detailData = await _titleRepository.GetTitleDetailAsync(title.Id, token);
            return detailData is null
                ? CatalogErrors.TitleNotFound(command.TitleId)
                : TitleDetailMapper.ToContract(detailData, systemKeys);
        }, _logger, ct);
    }

    private static TitleMetadataPayload MapToPayload(EnrichmentData data)
    {
        return new TitleMetadataPayload
        {
            Description = data.Description,
            Publisher = data.Publisher,
            Developer = data.Developer,
            Genre = data.Genre,
            ReleaseDate = data.ReleaseDate,
            Players = data.Players,
            Rating = data.Rating,
            ContentRatings = data.ContentRatings
        };
    }

}
