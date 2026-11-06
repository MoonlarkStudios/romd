using Romd.Domain.Libraries;

namespace Romd.Admin.Application.Libraries;

/// <summary>
///     Read-only title/release facts shared by draft evaluation and saved projections.
/// </summary>
public interface ILibraryCandidateReader
{
    Task<IReadOnlyList<TitleCandidates>> GetCandidatesAsync(
        LibraryConfiguration config,
        CancellationToken ct = default);
}
