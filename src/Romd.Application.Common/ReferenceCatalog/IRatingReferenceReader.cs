using Romd.Contracts.Common.ReferenceCatalog;

namespace Romd.Application.Common.ReferenceCatalog;

public interface IRatingReferenceReader
{
    Task<IReadOnlyList<RatingResourceDto>> ListAsync(CancellationToken ct);
    Task<ReferenceReadResult<RatingResourceDto>?> GetAsync(string key, CancellationToken ct);
}
