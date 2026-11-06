using Romd.Domain.Hashing;

namespace Romd.Admin.Application.Source.Platform;

/// <summary>Database-only acceptance facts inside the System Import owner's active transaction.</summary>
public interface ISystemDatImportStore
{
    /// <summary>Enables only the selected platform and holds its write lock until the owner settles.</summary>
    Task<bool> EnableStagedAsync(int platformId, CancellationToken ct = default);

    /// <summary>Checks installed documents globally, including other platforms and every lifecycle state.</summary>
    Task<bool> IsDocumentInstalledAsync(Sha256 hash, CancellationToken ct = default);
}
