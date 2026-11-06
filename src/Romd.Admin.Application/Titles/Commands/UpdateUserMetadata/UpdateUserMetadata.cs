using Romd.Application.Common.ReferenceCatalog;
using ErrorOr;
using Microsoft.Extensions.Logging;
using Romd.Admin.Application.Catalog;
using Romd.Admin.Application.Common.Persistence;
using Romd.Admin.Application.Libraries;
using Romd.Application.Common.Concurrency;
using Romd.Application.Common.Cqrs;
using Romd.Admin.Application.Titles.Enrichment;
using Romd.Admin.Application.Titles.ReadModels;
using Romd.Contracts.Management.Models;
using Romd.Domain.Catalog;

namespace Romd.Admin.Application.Titles.Commands.UpdateUserMetadata;

/// <summary>The user-layer metadata fields a partial update can supply.</summary>
[Flags]
public enum TitleMetadataFields
{
    None = 0,
    Name = 1 << 0,
    Description = 1 << 1,
    Publisher = 1 << 2,
    Developer = 1 << 3,
    Genre = 1 << 4,
    ReleaseDate = 1 << 5,
    Players = 1 << 6,
    Rating = 1 << 7
}

/// <summary>
///     Command to partially update user-authored metadata for a title. Only the <paramref name="Fields" /> it
///     supplies change: each takes its value from <paramref name="Values" />, where null removes that user override
///     (fall back to provider data). Every other user-layer field is kept.
///     <paramref name="IfMatch"/> is the title revision the editor read; a stale one writes nothing.
/// </summary>
public sealed record UpdateUserMetadataCommand(
    int TitleId,
    TitleMetadataPayload Values,
    TitleMetadataFields Fields,
    RevisionPrecondition IfMatch
) : ICommand<TitleDetail>;

/// <summary>
///     Handler for <see cref="UpdateUserMetadataCommand"/>.
///     Updates user-authored metadata and recalculates effective state.
/// </summary>
public sealed class UpdateUserMetadataCommandHandler : ICommandHandler<UpdateUserMetadataCommand, TitleDetail>
{
    private readonly TimeProvider _clock;
    private readonly IReferenceCatalogService _referenceCatalog;
    private readonly IUnitOfWork _unitOfWork;
    private readonly ITitleAggregateReader _titleAggregates;
    private readonly ITitleMetadataStore _titleMetadata;
    private readonly ITitleDetailReader _titleDetails;
    private readonly IMetadataRematerializer _metadataRematerializer;
    private readonly ILibraryRepository _libraryRepository;
    private readonly ILogger<UpdateUserMetadataCommandHandler> _logger;

    public UpdateUserMetadataCommandHandler(IReferenceCatalogService referenceCatalog,
        ITitleAggregateReader titleAggregates,
        ITitleMetadataStore titleMetadata,
        ITitleDetailReader titleDetails,
        IMetadataRematerializer metadataRematerializer,
        ILibraryRepository libraryRepository,
        IUnitOfWork unitOfWork,
        ILogger<UpdateUserMetadataCommandHandler> logger,
        TimeProvider clock)
    {
        _referenceCatalog = referenceCatalog;
        _unitOfWork = unitOfWork;
        _titleAggregates = titleAggregates;
        _titleMetadata = titleMetadata;
        _titleDetails = titleDetails;
        _metadataRematerializer = metadataRematerializer;
        _libraryRepository = libraryRepository;
        _logger = logger;
        _clock = clock;
    }

    public async Task<ErrorOr<TitleDetail>> HandleAsync(UpdateUserMetadataCommand command, CancellationToken ct = default)
    {
        var systemKeys = await _referenceCatalog.GetSystemKeysAsync(ct);
        try
        {
            var result = await _unitOfWork.ExecuteInTransactionAsync<TitleDetail>(async ct =>
            {
                // 1. Load title with all collections including metadata layers
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

                // 2. Merge the supplied fields into the existing user layer. Content rating claims live in the
                //    same layer but are authored through a separate command, so they carry forward with every
                //    field the patch leaves out.
                var existing = title.GetMetadataLayer("user")?.GetPayload() ?? new TitleMetadataPayload();
                var values = command.Values;
                bool Supplied(TitleMetadataFields field) => command.Fields.HasFlag(field);
                var payload = existing with
                {
                    Name = Supplied(TitleMetadataFields.Name) ? values.Name : existing.Name,
                    Description = Supplied(TitleMetadataFields.Description) ? values.Description : existing.Description,
                    Publisher = Supplied(TitleMetadataFields.Publisher) ? values.Publisher : existing.Publisher,
                    Developer = Supplied(TitleMetadataFields.Developer) ? values.Developer : existing.Developer,
                    Genre = Supplied(TitleMetadataFields.Genre) ? values.Genre : existing.Genre,
                    ReleaseDate = Supplied(TitleMetadataFields.ReleaseDate) ? values.ReleaseDate : existing.ReleaseDate,
                    Players = Supplied(TitleMetadataFields.Players) ? values.Players : existing.Players,
                    Rating = Supplied(TitleMetadataFields.Rating) ? values.Rating : existing.Rating
                };

                // 3. Store user metadata layer
                var previousEligibility = TitleEligibilityFields.From(title);
                title.StoreUserLayer(payload, _clock.GetUtcNow());

                // 4. Rematerialize with platform defaults before returning the effective title detail.
                await _metadataRematerializer.RematerializeAsync(title, ct);
                if (previousEligibility != TitleEligibilityFields.From(title))
                {
                    await _libraryRepository.FlagForRematerializationByPlatformAsync(title.PlatformId, ct);
                }

                // 5. Save changes
                await _titleMetadata.UpdateUserMetadataStagedAsync(title, ct);
                await _unitOfWork.FlushAsync(ct);

                // 6. Fetch full detail data for response
                var detailData = await _titleDetails.GetDetailAsync(title.Id, ct);
                if (detailData is null)
                {
                    // Shouldn't happen since we just updated it
                    return CatalogErrors.TitleNotFound();
                }

                // 7. Map to response with provenance
                return TitleDetailMapper.ToContract(detailData, systemKeys);
            }, _logger, ct);
            if (!result.IsError)
            {
                _logger.LogInformation("Updated title metadata (ID: {TitleId})", command.TitleId);
            }

            return result;
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
            _logger.LogError(ex, "Failed to update title metadata");
            return CatalogErrors.TitleUpdateFailed();
        }
    }
}
