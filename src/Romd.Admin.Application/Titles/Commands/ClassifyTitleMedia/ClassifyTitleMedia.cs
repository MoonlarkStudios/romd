using ErrorOr;
using Microsoft.Extensions.Logging;
using Romd.Admin.Application.Catalog;
using Romd.Admin.Application.Common.Persistence;
using Romd.Admin.Application.Titles.ReadModels;
using Romd.Application.Common.Concurrency;
using Romd.Application.Common.Cqrs;
using Romd.Application.Common.ReferenceCatalog;
using Romd.Contracts.Management.Models;
using Romd.Domain.Catalog;

namespace Romd.Admin.Application.Titles.Commands.ClassifyTitleMedia;

/// <summary>
///     Replaces one media item's type and placement: its canonical release (null for title level), region, language
///     and whether it is the preferred packaging scan of its type and release. <paramref name="IfMatch" /> is the
///     title revision the curator read.
/// </summary>
public sealed record ClassifyTitleMediaCommand(
    int TitleId,
    int MediaId,
    MediaType Type,
    MediaPlacement Placement,
    RevisionPrecondition IfMatch) : ICommand<TitleDetail>;

public sealed class ClassifyTitleMediaCommandHandler(
    IReferenceCatalogService referenceCatalog,
    ITitleAggregateReader titleAggregates,
    ITitleReader titleReader,
    ITitleMediaStore mediaStore,
    ITitleDetailReader titleDetails,
    IUnitOfWork unitOfWork,
    ILogger<ClassifyTitleMediaCommandHandler> logger) : ICommandHandler<ClassifyTitleMediaCommand, TitleDetail>
{
    public async Task<ErrorOr<TitleDetail>> HandleAsync(ClassifyTitleMediaCommand command, CancellationToken ct = default)
    {
        var systemKeys = await referenceCatalog.GetSystemKeysAsync(ct);
        Domain.Catalog.Title? title;
        try
        {
            title = await titleAggregates.GetAggregateAsync(command.TitleId, ct);
        }
        catch (PersistenceConflictException)
        {
            ct.ThrowIfCancellationRequested();
            return CatalogErrors.TitleReadConflict();
        }

        if (title is null)
            return CatalogErrors.TitleNotFound();
        if (!command.IfMatch.IsSatisfiedBy(title.Revision))
            return CatalogErrors.TitlePreconditionFailed();

        var outcome = title.ReclassifyMedia(command.MediaId, command.Type, command.Placement);
        if (outcome != MediaReclassification.Changed)
            return outcome switch
            {
                MediaReclassification.NotFound => CatalogErrors.MediaNotFound(),
                MediaReclassification.TypeLocked => CatalogErrors.MediaTypeLocked(),
                MediaReclassification.PreferenceNotSupported => CatalogErrors.MediaPreferenceNotSupported(),
                _ => CatalogErrors.InvalidMediaPlacement()
            };

        ErrorOr<TitleDetail> result;
        try
        {
            result = await unitOfWork.ExecuteInTransactionAsync<TitleDetail>(async token =>
            {
                // The staged write locks the snapshot revision first, so the release check below holds until commit.
                await mediaStore.ReclassifyMediaStagedAsync(title, token);
                if (command.Placement.CatalogReleaseId is { } releaseId &&
                    !await titleReader.HasCatalogReleaseAsync(title.Id, releaseId, token))
                    return CatalogErrors.MediaReleaseNotFound();
                await unitOfWork.FlushAsync(token);
                var detail = await titleDetails.GetDetailAsync(title.Id, token);
                return detail is null ? CatalogErrors.TitleNotFound() : TitleDetailMapper.ToContract(detail, systemKeys);
            }, logger, ct);
        }
        catch (PersistenceConflictException)
        {
            return CatalogErrors.TitlePreconditionFailed();
        }

        if (!result.IsError)
            logger.LogInformation("Classified media {MediaId} of title {TitleId} as {MediaType}", command.MediaId,
                title.Id, command.Type);
        return result;
    }
}
