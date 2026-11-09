using Romd.Contracts.Common.ReferenceCatalog;
using Romd.Contracts.Common.Models;

namespace Romd.Application.Common.ReferenceCatalog;

/// <summary>A reference resource and its revision: a hash of everything the resource and its overrides publish.</summary>
public sealed record ReferenceReadResult<TResource>(TResource Resource, string Revision);
public sealed record ReferenceResourceResult<TResource, TOverrides>(TResource Resource, TOverrides Overrides, string Revision);
public interface ISystemReferenceReader
{
    Task<Page<SystemResource>> ListAsync(ReferencePageRequest request, CancellationToken ct);
    Task<ReferenceResourceResult<SystemResource, SystemOverrides>?> GetAsync(string key, CancellationToken ct);
}
public interface ICompanyReferenceReader
{
    Task<Page<CompanyResource>> ListAsync(ReferencePageRequest request, CancellationToken ct);
    Task<ReferenceResourceResult<CompanyResource, CompanyOverrides>?> GetAsync(string key, CancellationToken ct);
}
