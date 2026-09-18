using Romd.Application.Common.ReferenceCatalog;
using ErrorOr;
using Microsoft.Extensions.Logging;
using Romd.Admin.Application.Catalog;
using Romd.Admin.Application.Common.Persistence;
using Romd.Admin.Application.Libraries;
using Romd.Application.Common.Cqrs;
using Romd.Admin.Application.Titles.Enrichment;
using Romd.Admin.Application.Titles.ReadModels;
using Romd.Contracts.Management.Models;
using Romd.Domain.Catalog;

namespace Romd.Admin.Application.Titles.Commands.UpdateUserMetadata;

/// <summary>
///     Command to update user-authored metadata for a title.
///     Null values mean "remove user override for this field" (fall back to provider data).
/// </summary>
public sealed record UpdateUserMetadataCommand(
    int TitleId,
    string? Name,
    string? Description,
    string? Publisher,
    string? Developer,
    string? Genre,
    DateOnly? ReleaseDate,
    int? Players,
    double? Rating
) : ICommand<TitleDetail>;

/// <summary>
///     Handler for <see cref="UpdateUserMetadataCommand"/>.
///     Updates user-authored metadata and recalculates effective state.
/// </summary>
public sealed class UpdateUserMetadataCommandHandler : ICommandHandler<UpdateUserMetadataCommand, TitleDetail>
{
    private readonly IReferenceCatalogService _referenceCatalog;
    private readonly IUnitOfWork _unitOfWork;
    private readonly ITitleRepository _titleRepository;
    private readonly IMetadataRematerializer _metadataRematerializer;
    private readonly ILibraryRepository _libraryRepository;
    private readonly ILogger<UpdateUserMetadataCommandHandler> _logger;

    public UpdateUserMetadataCommandHandler(IReferenceCatalogService referenceCatalog,
        ITitleRepository titleRepository,
        IMetadataRematerializer metadataRematerializer,
        ILibraryRepository libraryRepository,
        IUnitOfWork unitOfWork,
        ILogger<UpdateUserMetadataCommandHandler> logger)
    {
        _referenceCatalog = referenceCatalog;
        _unitOfWork = unitOfWork;
        _titleRepository = titleRepository;
        _metadataRematerializer = metadataRematerializer;
        _libraryRepository = libraryRepository;
        _logger = logger;
    }

    public async Task<ErrorOr<TitleDetail>> HandleAsync(UpdateUserMetadataCommand command, CancellationToken ct = default)
    {
        var systemKeys = await _referenceCatalog.GetSystemKeysAsync(ct);
        try
        {
            var result = await _unitOfWork.ExecuteInTransactionAsync<TitleDetail>(async ct =>
            {
                // 1. Load title with all collections including metadata layers
                var title = await _titleRepository.GetWithMetadataLayersAsync(command.TitleId, ct);
                if (title is null)
                {
                    return CatalogErrors.TitleNotFound(command.TitleId);
                }

                // 2. Create payload from command (null values mean "remove override").
                //    Content rating claims live in the same user layer but are authored through a
                //    separate command, so carry them forward instead of wiping them on a scalar edit.
                var existingUserRatings = title.GetMetadataLayer("user")?.GetPayload()?.ContentRatings;
                var payload = new TitleMetadataPayload
                {
                    Name = command.Name,
                    Description = command.Description,
                    Publisher = command.Publisher,
                    Developer = command.Developer,
                    Genre = command.Genre,
                    ReleaseDate = command.ReleaseDate,
                    Players = command.Players,
                    Rating = command.Rating,
                    ContentRatings = existingUserRatings
                };

                // 3. Store user metadata layer
                var previousEligibility = TitleEligibilityFields.From(title);
                title.StoreUserLayer(payload);

                // 4. Rematerialize with platform defaults before returning the effective title detail.
                await _metadataRematerializer.RematerializeAsync(title, ct);
                if (previousEligibility != TitleEligibilityFields.From(title))
                {
                    await _libraryRepository.FlagForRematerializationByPlatformAsync(title.PlatformId, ct);
                }

                // 5. Save changes
                await _titleRepository.UpdateUserMetadataStagedAsync(title, ct);
                await _unitOfWork.FlushAsync(ct);

                // 6. Fetch full detail data for response
                var detailData = await _titleRepository.GetTitleDetailAsync(title.Id, ct);
                if (detailData is null)
                {
                    // Shouldn't happen since we just updated it
                    return CatalogErrors.TitleNotFound(command.TitleId);
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
            return CatalogErrors.TitleConflict();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to update title metadata");
            return CatalogErrors.TitleUpdateFailed();
        }
    }
}
