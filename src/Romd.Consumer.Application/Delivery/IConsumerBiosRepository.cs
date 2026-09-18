using Romd.Domain.Hashing;
using Romd.Consumer.Application.Libraries;

namespace Romd.Consumer.Application.Delivery;

public sealed record ConsumerPlatformBiosRequest(
    ConsumerLibraryScope Scope,
    string PlatformShortName);

public sealed record ConsumerPlatformBios(
    int PlatformId,
    string PlatformShortName,
    bool IsExposed,
    IReadOnlyList<ConsumerBiosFile> Files);

public sealed record ConsumerBiosFile(
    int BiosId,
    string BiosName,
    string FileName,
    long SizeBytes,
    Sha1? Sha1,
    Md5? Md5,
    int? FileId,
    Sha256? Sha256,
    long? ContentSizeBytes)
{
    public bool IsAvailable => FileId is not null;
}

public interface IConsumerBiosRepository
{
    Task<ConsumerLibraryReadResult<ConsumerPlatformBios>> GetPlatformBiosAsync(
        ConsumerPlatformBiosRequest request,
        CancellationToken ct = default);
}
