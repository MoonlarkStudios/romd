using Romd.Contracts.Common.ReferenceCatalog;

namespace Romd.Application.Common.ReferenceCatalog;

public interface IRatingBoardReferenceReader
{
    Task<IReadOnlyList<RatingBoardResource>> ListAsync(CancellationToken ct);
    Task<ReferenceReadResult<RatingBoardResource>?> GetAsync(string key, CancellationToken ct);
}
