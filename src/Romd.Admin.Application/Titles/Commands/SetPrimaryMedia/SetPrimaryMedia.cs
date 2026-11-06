using Romd.Application.Common.ReferenceCatalog;
using ErrorOr;
using Microsoft.Extensions.Logging;
using Romd.Admin.Application.Catalog;
using Romd.Admin.Application.Common.Persistence;
using Romd.Application.Common.Concurrency;
using Romd.Application.Common.Cqrs;
using Romd.Admin.Application.Titles.ReadModels;
using Romd.Contracts.Management.Models;

namespace Romd.Admin.Application.Titles.Commands.SetPrimaryMedia;

/// <summary>
///     Command to manually set a specific media as the primary for its type.
///     This overrides the automatic priority-based selection.
/// </summary>
public sealed record SetPrimaryMediaCommand(
    int TitleId,
    int MediaId,
    RevisionPrecondition IfMatch
) : ICommand<TitleDetail>;

/// <summary>
///     Handler for <see cref="SetPrimaryMediaCommand"/>.
///     Manually sets a specific media as primary.
/// </summary>
public sealed class SetPrimaryMediaCommandHandler : ICommandHandler<SetPrimaryMediaCommand, TitleDetail>
{
    private readonly IReferenceCatalogService _referenceCatalog;
    private readonly ITitleAggregateReader _titleAggregates;
    private readonly ITitleMediaStore _mediaStore;
    private readonly ITitleDetailReader _titleDetails;
    private readonly IUnitOfWork _unitOfWork;
    private readonly ILogger<SetPrimaryMediaCommandHandler> _logger;

    public SetPrimaryMediaCommandHandler(IReferenceCatalogService referenceCatalog,
        ITitleAggregateReader titleAggregates,
        ITitleMediaStore mediaStore,
        ITitleDetailReader titleDetails,
        IUnitOfWork unitOfWork,
        ILogger<SetPrimaryMediaCommandHandler> logger)
    {
        _referenceCatalog = referenceCatalog;
        _titleAggregates = titleAggregates;
        _mediaStore = mediaStore;
        _titleDetails = titleDetails;
        _unitOfWork = unitOfWork;
        _logger = logger;
    }

    public async Task<ErrorOr<TitleDetail>> HandleAsync(SetPrimaryMediaCommand command, CancellationToken ct = default)
    {
        var systemKeys = await _referenceCatalog.GetSystemKeysAsync(ct);
        Domain.Catalog.Title? title;
        try
        {
            title = await _titleAggregates.GetAggregateAsync(command.TitleId, ct);
        }
        catch (PersistenceConflictException)
        {
            ct.ThrowIfCancellationRequested();
            return CatalogErrors.TitleReadConflict();
        }
        if (title is null)
        {
            return CatalogErrors.TitleNotFound();
        }

        if (!command.IfMatch.IsSatisfiedBy(title.Revision))
        {
            return CatalogErrors.TitlePreconditionFailed();
        }

        var media = title.GetMediaById(command.MediaId);
        if (media is null)
        {
            return CatalogErrors.MediaNotFound();
        }

        // Clears the other primaries of the same type. The staged write rejects a stale snapshot.
        title.SetPrimaryMedia(command.MediaId);

        ErrorOr<TitleDetail> result;
        try
        {
            result = await _unitOfWork.ExecuteInTransactionAsync<TitleDetail>(async token =>
            {
                await _mediaStore.UpdatePrimaryMediaStagedAsync(title, media.Type, token);
                // The response reads the selection inside the transaction, so a failed read rolls the
                // selection back instead of reporting a committed change as failed.
                await _unitOfWork.FlushAsync(token);
                var detailData = await _titleDetails.GetDetailAsync(title.Id, token);
                return detailData is null
                    ? CatalogErrors.TitleNotFound()
                    : TitleDetailMapper.ToContract(detailData, systemKeys);
            }, _logger, ct);
        }
        catch (PersistenceConflictException)
        {
            return CatalogErrors.TitlePreconditionFailed();
        }

        if (!result.IsError)
        {
            _logger.LogInformation(
                "Set {MediaType} media (ID: {MediaId}) as primary for title '{TitleName}' (ID: {TitleId})",
                media.Type, command.MediaId, title.Name, title.Id);
        }

        return result;
    }
}
