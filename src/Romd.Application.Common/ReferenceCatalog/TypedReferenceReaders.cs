using Romd.Contracts.Common.ReferenceCatalog;

namespace Romd.Application.Common.ReferenceCatalog;

public sealed record ReferenceReadResult<TResource>(TResource Resource, string ETag);
public sealed record ReferenceResourceResult<TResource, TOverrides>(TResource Resource, TOverrides Overrides, string ETag);
public interface ISystemReferenceReader
{
    Task<IReadOnlyList<SystemResourceDto>> ListAsync(CancellationToken ct);
    Task<ReferenceResourceResult<SystemResourceDto, SystemOverridesDto>?> GetAsync(string key, CancellationToken ct);
}
public interface ICompanyReferenceReader
{
    Task<IReadOnlyList<CompanyResourceDto>> ListAsync(CancellationToken ct);
    Task<ReferenceResourceResult<CompanyResourceDto, CompanyOverridesDto>?> GetAsync(string key, CancellationToken ct);
}
