using Romd.Contracts.Common.ReferenceCatalog;

namespace Romd.Application.Common.ReferenceCatalog;

public interface IRatingReferenceReader
{
    Task<IReadOnlyList<RatingResource>> ListAsync(CancellationToken ct);
    Task<ReferenceReadResult<RatingResource>?> GetAsync(string key, CancellationToken ct);
}
