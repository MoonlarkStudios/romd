using Romd.Domain.Hashing;

namespace Romd.Admin.Application.Hashing;

public interface IHashingService
{
    Task<DatHashResult> ComputeDatHashesAsync(Stream stream, CancellationToken ct = default);
}

public sealed record DatHashResult(
    Sha1 Sha1,
    Md5 Md5,
    Crc32 Crc32,
    long BytesRead);
