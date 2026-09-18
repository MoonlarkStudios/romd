using Romd.Contracts.Common.ReferenceCatalog;

namespace Romd.Application.Common.ReferenceCatalog;

public interface ILanguageReferenceReader
{
    Task<IReadOnlyList<LanguageResourceDto>> ListAsync(CancellationToken ct);
    Task<ReferenceReadResult<LanguageResourceDto>?> GetAsync(string key, CancellationToken ct);
}
