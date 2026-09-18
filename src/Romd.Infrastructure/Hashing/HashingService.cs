using Romd.Admin.Application.Hashing;

namespace Romd.Infrastructure.Hashing;

public sealed class HashingService : IHashingService
{
    public async Task<DatHashResult> ComputeDatHashesAsync(Stream stream, CancellationToken ct = default)
    {
        using var hashStream = new MultiHashReadStream(stream, HashAlgorithms.DatHashes, leaveOpen: true);
        var buffer = new byte[81920];
        int read;
        while ((read = await hashStream.ReadAsync(buffer.AsMemory(0, buffer.Length), ct)) > 0)
        {
        }

        var hashes = hashStream.GetComputedHashes();
        return new DatHashResult(hashes.Sha1, hashes.Md5, hashes.Crc32, hashStream.BytesRead);
    }
}
