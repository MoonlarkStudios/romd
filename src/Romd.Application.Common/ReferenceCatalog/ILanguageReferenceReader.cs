using Romd.Contracts.Common.ReferenceCatalog;

namespace Romd.Application.Common.ReferenceCatalog;

public interface ILanguageReferenceReader
{
    Task<IReadOnlyList<LanguageResource>> ListAsync(CancellationToken ct);
    Task<ReferenceReadResult<LanguageResource>?> GetAsync(string key, CancellationToken ct);
}
