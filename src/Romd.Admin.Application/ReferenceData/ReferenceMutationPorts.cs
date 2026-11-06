using ErrorOr;
using Romd.Domain.ReferenceData;

namespace Romd.Admin.Application.ReferenceData;

/// <summary>Serializes conditional writes and atomically publishes the effective catalog.</summary>
public interface IReferenceMutationSession
{
    Task FlushAsync(CancellationToken ct);
    Task<ErrorOr<T>> RunAsync<T>(Func<CancellationToken, Task<ErrorOr<T>>> operation, CancellationToken ct, bool protectDependencies = false);
}
public interface ISystemReferenceRepository
{
    Task<SystemReference?> GetAsync(string key, CancellationToken ct);
    Task<bool> KeyExistsAsync(string key, CancellationToken ct);
    Task StageAsync(SystemReference system, CancellationToken ct);
    Task<bool> HasDependentsAsync(int id, CancellationToken ct);
    Task DeleteAsync(int id, CancellationToken ct);
    Task<bool> AssetExistsAsync(string hash, CancellationToken ct);
}
public interface ICompanyReferenceRepository
{
    Task<CompanyReference?> GetAsync(string key, CancellationToken ct);
    Task<bool> AllExistAsync(IReadOnlyList<string> keys, CancellationToken ct);
    Task StageAsync(CompanyReference company, CancellationToken ct);
    Task<bool> HasDependentsAsync(string key, CancellationToken ct);
    Task DeleteAsync(string key, CancellationToken ct);
}
