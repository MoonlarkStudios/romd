using Microsoft.Extensions.Logging;

namespace Romd.Admin.Application.Titles.Enrichment;

/// <summary>
///     Owns staged media per title until each title's share is handed to the caller in its result.
///     Disposing the ledger releases everything not yet handed off, so a failure, cancellation or an
///     abandoned stream never leaks staged files or downloads. Release is best-effort cleanup: a failed
///     release is logged and the rest are still released, and disposal never throws, so it cannot replace
///     the primary outcome (for example turn a cancellation into a failure).
/// </summary>
internal sealed class StagedMediaLedger(IEnumerable<int> titleIds, ILogger logger) : IAsyncDisposable
{
    private readonly Dictionary<int, List<StagedTitleMedia>> _media =
        titleIds.ToDictionary(id => id, _ => new List<StagedTitleMedia>());

    public ICollection<StagedTitleMedia> For(int titleId) => _media[titleId];

    /// <summary>Transfers ownership of a title's staged media to the caller.</summary>
    public IReadOnlyList<StagedTitleMedia> HandOff(int titleId) =>
        _media.Remove(titleId, out var media) ? media : [];

    public async ValueTask DisposeAsync()
    {
        foreach (var (titleId, media) in _media)
        {
            foreach (var item in media)
            {
                try
                {
                    await item.DisposeAsync();
                }
                catch (Exception exception)
                {
                    logger.LogWarning(exception,
                        "Could not release staged {MediaType} media for title {TitleId}", item.Type, titleId);
                }
            }
        }

        _media.Clear();
    }
}
