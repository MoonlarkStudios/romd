using Romd.Application.Common.ReferenceCatalog;
using ErrorOr;
using Microsoft.Extensions.Logging;
using Romd.Admin.Application.Catalog;
using Romd.Admin.Application.Common.Persistence;
using Romd.Admin.Application.Libraries;
using Romd.Admin.Application.Titles.Enrichment;
using Romd.Admin.Application.Titles.ReadModels;
using Romd.Application.Common.Concurrency;
using Romd.Application.Common.Cqrs;
using Romd.Contracts.Management.Models;

namespace Romd.Admin.Application.Titles.Commands.SetFieldOverrides;

/// <summary><paramref name="IfMatch"/> is the title revision the editor read; a stale one writes nothing.</summary>
public sealed record SetFieldOverridesCommand(
    int TitleId,
    IReadOnlyDictionary<string, string?> Overrides,
    RevisionPrecondition IfMatch) : ICommand<TitleDetail>;

public sealed class SetFieldOverridesCommandHandler
    : ICommandHandler<SetFieldOverridesCommand, TitleDetail>
{
    private readonly IReferenceCatalogService _referenceCatalog;
    private readonly ITitleAggregateReader _titleAggregates;
    private readonly ITitleMetadataStore _titleMetadata;
    private readonly ITitleDetailReader _titleDetails;
    private readonly IMetadataRematerializer _metadataRematerializer;
    private readonly ILibraryRepository _libraryRepository;
    private readonly IUnitOfWork _unitOfWork;
    private readonly ILogger<SetFieldOverridesCommandHandler> _logger;

    public SetFieldOverridesCommandHandler(IReferenceCatalogService referenceCatalog,
        ITitleAggregateReader titleAggregates,
        ITitleMetadataStore titleMetadata,
        ITitleDetailReader titleDetails,
        IMetadataRematerializer metadataRematerializer,
        ILibraryRepository libraryRepository,
        IUnitOfWork unitOfWork,
        ILogger<SetFieldOverridesCommandHandler> logger)
    {
        _referenceCatalog = referenceCatalog;
        _titleAggregates = titleAggregates;
        _titleMetadata = titleMetadata;
        _titleDetails = titleDetails;
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
                var title = await _titleAggregates.GetAggregateAsync(command.TitleId, ct);
                if (title is null)
                {
                    return CatalogErrors.TitleNotFound();
                }

                // The staged write re-verifies this snapshot, so a concurrent write caught at save time is the
                // same 412 and a stale editor has one reload path.
                if (!command.IfMatch.IsSatisfiedBy(title.Revision))
                {
                    return CatalogErrors.TitlePreconditionFailed();
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

                await _titleMetadata.UpdateMaterializedMetadataStagedAsync(title, ct);
                await _unitOfWork.FlushAsync(ct);

                var detailData = await _titleDetails.GetDetailAsync(title.Id, ct);
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
            return CatalogErrors.TitlePreconditionFailed();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to set title field overrides");
            return CatalogErrors.TitleUpdateFailed();
        }
    }
}
