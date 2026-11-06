using Romd.Admin.Application.Titles.ReadModels;

namespace Romd.Admin.Application.Titles;

/// <summary>Read port for a title's current media rows, without loading the title aggregate.</summary>
public interface ITitleMediaReader
{
    /// <summary>
    ///     Lists the title's media ordered by ID. Empty when the title has no media or does not exist.
    /// </summary>
    Task<IReadOnlyList<TitleMediaSummary>> ListAsync(int titleId, CancellationToken cancellationToken = default);
}
