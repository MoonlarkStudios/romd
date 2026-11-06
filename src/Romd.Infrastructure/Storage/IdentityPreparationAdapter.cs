using ErrorOr;
using Romd.Admin.Application.Hashing;
using Romd.Admin.Application.Source.Rom;
using Romd.Admin.Application.Storage.Files;
using Romd.Domain.Source.Rom;

namespace Romd.Infrastructure.Storage;

internal sealed class IdentityPreparationAdapter : IRomPreparationAdapter
{
    private readonly IHashingService _hashing;
    public IdentityPreparationAdapter(IHashingService hashing) => _hashing = hashing;
    public RomRepresentationEncoding Encoding => RomRepresentationEncoding.Identity;
    public ReadOnlySpan<byte> Signature => ReadOnlySpan<byte>.Empty;
    public bool RequiresDatHashes(ReadOnlySpan<byte> prefix) => true;
    public bool Claims(ReadOnlySpan<byte> magic, string filename) => true;
    public Task<IRomPreparationAdmission?> AdmitAsync(ReadOnlyMemory<byte> magic, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult<IRomPreparationAdmission?>(null);
    }
    public async Task<ErrorOr<PreparedRom>> VerifyAsync(ITempFile source, string filename, CancellationToken ct)
    {
        var hashes = source.DatHashes;
        if (hashes is null)
        {
            await using var input = source.OpenRead();
            hashes = await _hashing.ComputeDatHashesAsync(input, ct);
        }
        var proof = new RomVerification(source.FileSha256, source.FileSize, Encoding, source.FileSha256,
            hashes.BytesRead, hashes.Sha1, hashes.Md5, hashes.Crc32, "identity/v1", "identity/v1",
            "romd-media/mvid:" + typeof(IdentityPreparationAdapter).Module.ModuleVersionId, DateTimeOffset.UtcNow);
        return new PreparedRom.WholeFile(source, filename, source.FileSha256, hashes, Encoding, proof, RomContainerFacts.Identity.Instance, encodingProfile: "identity/v1");
    }
}
