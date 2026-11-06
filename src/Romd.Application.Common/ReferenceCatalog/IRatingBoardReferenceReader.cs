using Romd.Contracts.Common.ReferenceCatalog;

namespace Romd.Application.Common.ReferenceCatalog;

public interface IRatingBoardReferenceReader
{
    Task<IReadOnlyList<RatingBoardResourceDto>> ListAsync(CancellationToken ct);
    Task<ReferenceReadResult<RatingBoardResourceDto>?> GetAsync(string key, CancellationToken ct);
}
