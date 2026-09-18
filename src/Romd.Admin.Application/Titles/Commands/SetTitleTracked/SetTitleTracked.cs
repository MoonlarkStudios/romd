using ErrorOr;
using Romd.Admin.Application.Catalog;
using Romd.Application.Common.Cqrs;

namespace Romd.Admin.Application.Titles.Commands.SetTitleTracked;

/// <summary>
///     Sets whether a title has durable tracked collection intent.
///     Tracking a title includes it in coverage and the default enrichment scope; untracking
///     removes it, even when the title is owned.
/// </summary>
public sealed record SetTitleTrackedCommand(
    int TitleId,
    bool Tracked,
    int? PinnedCatalogReleaseId = null) : ICommand<Updated>;

/// <summary>
///     Handler for <see cref="SetTitleTrackedCommand" />.
/// </summary>
public sealed class SetTitleTrackedCommandHandler(ITrackedTitleRepository trackedTitleRepository)
    : ICommandHandler<SetTitleTrackedCommand, Updated>
{
    public async Task<ErrorOr<Updated>> HandleAsync(SetTitleTrackedCommand command, CancellationToken ct = default)
    {
        if (!command.Tracked)
        {
            bool found = await trackedTitleRepository.UntrackAsync(command.TitleId, ct);
            return found ? Result.Updated : CatalogErrors.TitleNotFound(command.TitleId);
        }

        var result = await trackedTitleRepository.TrackAsync(command.TitleId, command.PinnedCatalogReleaseId, ct);
        return result switch
        {
            TrackTitleResult.Updated => Result.Updated,
            TrackTitleResult.TitleNotFound => CatalogErrors.TitleNotFound(command.TitleId),
            TrackTitleResult.PinnedCatalogReleaseNotFound =>
                CatalogErrors.CatalogReleaseNotFound(command.PinnedCatalogReleaseId!.Value),
            TrackTitleResult.PinnedCatalogReleaseTitleMismatch =>
                CatalogErrors.PinnedReleaseTitleMismatch(command.PinnedCatalogReleaseId!.Value, command.TitleId),
            _ => throw new InvalidOperationException($"Unknown tracking result: {result}")
        };
    }
}
