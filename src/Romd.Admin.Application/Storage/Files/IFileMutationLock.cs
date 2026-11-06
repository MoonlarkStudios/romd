using Romd.Domain.Hashing;

namespace Romd.Admin.Application.Storage.Files;

/// <summary>
/// Serializes CAS publication and cleanup across processes. The caller owns a
/// transaction through its final CAS operation and metadata commit. Acquire
/// multiple hashes in ordinal hexadecimal order to prevent opposing lock order.
/// </summary>
public interface IFileMutationLock
{
    /// <summary>Captures the caller's current transaction without acquiring a lock or flushing state.</summary>
    IFilePublicationBoundary CapturePublicationBoundary();

    Task AcquireAsync(Sha256 hash, CancellationToken ct = default);
}
