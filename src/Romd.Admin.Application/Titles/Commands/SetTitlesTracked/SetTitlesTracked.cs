using ErrorOr;
using Romd.Admin.Application.Catalog;
using Romd.Application.Common.Cqrs;

namespace Romd.Admin.Application.Titles.Commands.SetTitlesTracked;

/// <summary>
///     Sets the tracked state of many titles at once. Used by the catalog's bulk-track affordance.
/// </summary>
public sealed record SetTitlesTrackedCommand(
    IReadOnlyList<int>? TitleIds,
    int? PlatformId,
    int? DatSourceId,
    bool Tracked) : ICommand<Updated>;

/// <summary>
///     Handler for <see cref="SetTitlesTrackedCommand" />.
/// </summary>
public sealed class SetTitlesTrackedCommandHandler(ITrackedTitleRepository trackedTitleRepository)
    : ICommandHandler<SetTitlesTrackedCommand, Updated>
{
    public async Task<ErrorOr<Updated>> HandleAsync(SetTitlesTrackedCommand command, CancellationToken ct = default)
    {
        int selectorCount = (command.TitleIds is { Count: > 0 } ? 1 : 0)
            + (command.PlatformId.HasValue ? 1 : 0)
            + (command.DatSourceId.HasValue ? 1 : 0);
        if (selectorCount != 1)
        {
            return CatalogErrors.InvalidTrackingSelector();
        }

        if (command.TitleIds is { Count: > 0 } titleIds)
        {
            if (command.Tracked)
            {
                await trackedTitleRepository.TrackByTitleIdsAsync(titleIds, ct);
            }
            else
            {
                await trackedTitleRepository.UntrackByTitleIdsAsync(titleIds, ct);
            }
        }
        else if (command.PlatformId is int platformId)
        {
            if (command.Tracked)
            {
                await trackedTitleRepository.TrackByPlatformAsync(platformId, ct);
            }
            else
            {
                await trackedTitleRepository.UntrackByPlatformAsync(platformId, ct);
            }
        }
        else if (command.DatSourceId is int datSourceId)
        {
            if (command.Tracked)
            {
                await trackedTitleRepository.TrackByDatSourceAsync(datSourceId, ct);
            }
            else
            {
                await trackedTitleRepository.UntrackByDatSourceAsync(datSourceId, ct);
            }
        }

        return Result.Updated;
    }
}
