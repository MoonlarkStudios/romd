using Romd.Application.Common.ReferenceCatalog;
using ErrorOr;
using Microsoft.Extensions.Logging;
using Romd.Admin.Application.Catalog;
using Romd.Admin.Application.Common.Persistence;
using Romd.Admin.Application.Libraries;
using Romd.Admin.Application.Titles.Enrichment;
using Romd.Admin.Application.Titles.ReadModels;
using Romd.Application.Common.Cqrs;
using Romd.Contracts.Management.Models;

namespace Romd.Admin.Application.Titles.Commands.SetFieldOverrides;

public sealed record SetFieldOverridesCommand(
    int TitleId,
    IReadOnlyDictionary<string, string?> Overrides) : ICommand<TitleDetail>;

public sealed class SetFieldOverridesCommandHandler
    : ICommandHandler<SetFieldOverridesCommand, TitleDetail>
{
    private readonly IReferenceCatalogService _referenceCatalog;
    private readonly ITitleRepository _titleRepository;
    private readonly IMetadataRematerializer _metadataRematerializer;
    private readonly ILibraryRepository _libraryRepository;
    private readonly IUnitOfWork _unitOfWork;
    private readonly ILogger<SetFieldOverridesCommandHandler> _logger;

    public SetFieldOverridesCommandHandler(IReferenceCatalogService referenceCatalog,
        ITitleRepository titleRepository,
        IMetadataRematerializer metadataRematerializer,
        ILibraryRepository libraryRepository,
        IUnitOfWork unitOfWork,
        ILogger<SetFieldOverridesCommandHandler> logger)
    {
        _referenceCatalog = referenceCatalog;
        _titleRepository = titleRepository;
        _metadataRematerializer = metadataRematerializer;
        _libraryRepository = libraryRepository;
        _unitOfWork = unitOfWork;
        _logger = logger;
    }

    public async Task<ErrorOr<TitleDetail>> HandleAsync(
        SetFieldOverridesCommand command,
        CancellationToken ct = default)
    {
        var systemKeys = await _referenceCatalog.GetSystemKeysAsync(ct);
        try
        {
            return await _unitOfWork.ExecuteInTransactionAsync<TitleDetail>(async ct =>
            {
                var title = await _titleRepository.GetWithMetadataLayersAsync(command.TitleId, ct);
                if (title is null)
                {
                    return CatalogErrors.TitleNotFound();
                }

                var previousEligibility = TitleEligibilityFields.From(title);
                foreach ((string fieldName, string? sourceId) in command.Overrides)
                {
                    if (string.IsNullOrEmpty(sourceId))
                    {
                        title.ClearFieldSourceOverride(fieldName);
                    }
                    else
                    {
                        title.SetFieldSourceOverride(fieldName, sourceId);
                    }
                }

                await _metadataRematerializer.RematerializeAsync(title, ct);
                if (previousEligibility != TitleEligibilityFields.From(title))
                {
                    await _libraryRepository.FlagForRematerializationByPlatformAsync(title.PlatformId, ct);
                }

                await _titleRepository.UpdateMaterializedMetadataStagedAsync(title, ct);
                await _unitOfWork.FlushAsync(ct);

                var detailData = await _titleRepository.GetTitleDetailAsync(title.Id, ct);
                if (detailData is null)
                {
                    return CatalogErrors.TitleNotFound();
                }

                return TitleDetailMapper.ToContract(detailData, systemKeys);
            }, _logger, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (PersistenceConflictException)
        {
            return CatalogErrors.TitleConflict();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to set title field overrides");
            return CatalogErrors.TitleUpdateFailed();
        }
    }
}
