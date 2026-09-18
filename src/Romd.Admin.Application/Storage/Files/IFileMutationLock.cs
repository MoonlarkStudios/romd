using Romd.Domain.Hashing;

namespace Romd.Admin.Application.Storage.Files;

/// <summary>
/// Serializes CAS publication and cleanup across processes. The caller owns a
/// transaction through its final CAS operation and metadata commit. Acquire
/// multiple hashes in ordinal hexadecimal order to prevent opposing lock order.
/// </summary>
public interface IFileMutationLock
{
    Task AcquireAsync(Sha256 hash, CancellationToken ct = default);
}
