using ErrorOr;
using Romd.Domain.Hashing;

namespace Romd.Admin.Application.Storage.Files;

/// <summary>Raw artifact facts measured after encoding; these are not logical ROM evidence.</summary>
public sealed record TempFileArtifactMeasurement(Sha256 Sha256, Md5 Md5, Sha1 Sha1, long Size);

/// <summary>A fully reserved output path. It becomes a readable artifact only after the child has stopped.</summary>
public interface IWritableTempFile : IAsyncDisposable
{
    string Path { get; }
    long MaximumBytes { get; }
    bool TryBeginChildUse();
    bool TryEndChildUse();
    bool Quarantine();
    Task<ErrorOr<ITempFile>> SealAsync(TempFileArtifactMeasurement measurement, CancellationToken ct = default);
}
