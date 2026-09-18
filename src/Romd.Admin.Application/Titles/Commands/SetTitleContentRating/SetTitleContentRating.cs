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
using Romd.Domain.Catalog.Ratings;
using RatingBoard = Romd.Domain.Catalog.Ratings.RatingBoard;

namespace Romd.Admin.Application.Titles.Commands.SetTitleContentRating;

/// <summary>
///     Command to set or clear the user-authored content rating for a single board.
///     A null/blank <see cref="Code" /> clears the user override for the board, reverting it to
///     the provider cascade. The override is a user-layer claim — it wins the cascade for its
///     board, flows through the same policy enforcement, and leaves "user" provenance.
/// </summary>
public sealed record SetTitleContentRatingCommand(
    int TitleId,
    RatingBoard Board,
    string? Code,
    IReadOnlyList<string>? Descriptors,
    string? Synopsis) : ICommand<TitleDetail>;

/// <summary>
///     Handler for <see cref="SetTitleContentRatingCommand" />. Mutates the user metadata layer,
///     rematerializes effective ratings, and flags affected libraries when eligibility changes.
/// </summary>
public sealed class SetTitleContentRatingCommandHandler
    : ICommandHandler<SetTitleContentRatingCommand, TitleDetail>
{
    private readonly IReferenceCatalogService _referenceCatalog;
    private readonly IUnitOfWork _unitOfWork;
    private readonly ITitleRepository _titleRepository;
    private readonly IMetadataRematerializer _metadataRematerializer;
    private readonly ILibraryRepository _libraryRepository;
    private readonly ILogger<SetTitleContentRatingCommandHandler> _logger;

    public SetTitleContentRatingCommandHandler(IReferenceCatalogService referenceCatalog,
        ITitleRepository titleRepository,
        IMetadataRematerializer metadataRematerializer,
        ILibraryRepository libraryRepository,
        IUnitOfWork unitOfWork,
        ILogger<SetTitleContentRatingCommandHandler> logger)
    {
        _referenceCatalog = referenceCatalog;
        _unitOfWork = unitOfWork;
        _titleRepository = titleRepository;
        _metadataRematerializer = metadataRematerializer;
        _libraryRepository = libraryRepository;
        _logger = logger;
    }

    public async Task<ErrorOr<TitleDetail>> HandleAsync(
        SetTitleContentRatingCommand command,
        CancellationToken ct = default)
    {
        var systemKeys = await _referenceCatalog.GetSystemKeysAsync(ct);
        try
        {
            var result = await _unitOfWork.ExecuteInTransactionAsync<TitleDetail>(async ct =>
            {
                var title = await _titleRepository.GetWithMetadataLayersAsync(command.TitleId, ct);
                if (title is null)
                {
                    return CatalogErrors.TitleNotFound(command.TitleId);
                }

                var previousEligibility = TitleEligibilityFields.From(title);

                if (string.IsNullOrWhiteSpace(command.Code))
                {
                    title.ClearUserContentRating(command.Board);
                }
                else
                {
                    if (RatingBoardCatalog.TryResolve(command.Board, command.Code) is null)
                    {
                        return CatalogErrors.InvalidContentRating(command.Board, command.Code);
                    }

                    title.SetUserContentRating(new ContentRatingClaim
                    {
                        Board = command.Board,
                        RawCode = command.Code,
                        Descriptors = command.Descriptors ?? [],
                        Synopsis = string.IsNullOrWhiteSpace(command.Synopsis) ? null : command.Synopsis
                    });
                }

                await _metadataRematerializer.RematerializeAsync(title, ct);
                if (previousEligibility != TitleEligibilityFields.From(title))
                {
                    await _libraryRepository.FlagForRematerializationByPlatformAsync(title.PlatformId, ct);
                }

                await _titleRepository.UpdateUserMetadataStagedAsync(title, ct);
                await _unitOfWork.FlushAsync(ct);

                var detailData = await _titleRepository.GetTitleDetailAsync(title.Id, ct);
                if (detailData is null)
                {
                    return CatalogErrors.TitleNotFound(command.TitleId);
                }

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
