using Romd.Domain.Hashing;

namespace Romd.Admin.Application.Storage.Files;

/// <summary>
///     Content encoded for publication by <see cref="IFileStorageService" />. Owned by one caller;
///     disposal discards whatever publication did not use.
/// </summary>
public interface IStagedFile : IAsyncDisposable
{
    Sha256 Sha256 { get; }

    /// <summary>Uncompressed length.</summary>
    long Size { get; }
    /// <summary>Hashes of a complete measurement of the staged artifact, or null when unmeasured.</summary>
    Md5? ArtifactMd5 { get; }
    Sha1? ArtifactSha1 { get; }
}
