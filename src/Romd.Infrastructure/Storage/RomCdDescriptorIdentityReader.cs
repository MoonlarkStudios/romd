using System.Security.Cryptography;
using Romd.Admin.Application.Source.Rom;
using Romd.Admin.Application.Storage.Files;
using Romd.Domain.Hashing;
using Romd.Domain.Source.Rom;
using Romd.Media.Cd;

namespace Romd.Infrastructure.Storage;

public sealed class RomCdDescriptorIdentityReader(IFileStorageService files) : IRomCdDescriptorIdentityReader
{
    public async Task<RomCdFileIdentity?> ReadAsync(int fileId, Sha256 expectedSha256, long expectedSize, CancellationToken ct)
    {
        if (expectedSize is < 1 or > ChdCdCue.MaximumBytes)
            return null;
        await using var input = await files.RetrieveByIdAsync(fileId, ct);
        if (input is null)
            return null;
        byte[] bytes = new byte[checked((int)expectedSize)];
        await input.ReadExactlyAsync(bytes, ct);
        if (await input.ReadAsync(new byte[1], ct) != 0 || Sha256.FromSpan(SHA256.HashData(bytes)) != expectedSha256)
            return null;
        _ = ChdCdCue.Parse(bytes);
        return new(expectedSize, expectedSha256, Sha1.FromSpan(SHA1.HashData(bytes)), Md5.FromSpan(MD5.HashData(bytes)),
            Crc32.FromUInt32(System.IO.Hashing.Crc32.HashToUInt32(bytes)));
    }
}
