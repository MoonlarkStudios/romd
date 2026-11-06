using Romd.Contracts.Common.ReferenceCatalog;
using Romd.Contracts.Common.Models;

namespace Romd.Application.Common.ReferenceCatalog;

/// <summary>A reference resource and its revision: a hash of everything the resource and its overrides publish.</summary>
public sealed record ReferenceReadResult<TResource>(TResource Resource, string Revision);
public sealed record ReferenceResourceResult<TResource, TOverrides>(TResource Resource, TOverrides Overrides, string Revision);
public interface ISystemReferenceReader
{
    Task<Page<SystemResourceDto>> ListAsync(ReferencePageRequest request, CancellationToken ct);
    Task<ReferenceResourceResult<SystemResourceDto, SystemOverridesDto>?> GetAsync(string key, CancellationToken ct);
}
public interface ICompanyReferenceReader
{
    Task<Page<CompanyResourceDto>> ListAsync(ReferencePageRequest request, CancellationToken ct);
    Task<ReferenceResourceResult<CompanyResourceDto, CompanyOverridesDto>?> GetAsync(string key, CancellationToken ct);
}
