using ErrorOr;
using Romd.Admin.Application.Source.Rom;
using Romd.Admin.Application.Storage.Files;
using Romd.Domain.Source.Rom;

namespace Romd.Infrastructure.Storage;

internal sealed class RvzPreparationAdapter : IRomPreparationAdapter
{
    private readonly Func<RvzVerifier> _verifier;
    public RvzPreparationAdapter(Func<RvzVerifier> verifier) => _verifier = verifier;
    public RomRepresentationEncoding Encoding => RomRepresentationEncoding.Rvz;
    public bool RequiresDatHashes(ReadOnlySpan<byte> prefix) => false;
    public ReadOnlySpan<byte> Signature => RomPreparationDetection.Signature(Encoding).Magic;
    public bool Claims(ReadOnlySpan<byte> magic, string filename) => magic.StartsWith(Signature) ||
        RomPreparationDetection.Signature(Encoding).ClaimsFilename(filename);
    public async Task<IRomPreparationAdmission?> AdmitAsync(ReadOnlyMemory<byte> magic, CancellationToken ct)
    {
        if (!magic.Span.StartsWith(Signature))
            throw new InvalidDataException("Invalid RVZ header.");
        return new Admission(this, await _verifier().AcquireAsync(ct));
    }
    public Task<ErrorOr<PreparedRom>> VerifyAsync(ITempFile source, string filename, CancellationToken ct) =>
        VerifyAsync(source, filename, (input, token) => _verifier().VerifyAsync(input, token), ct);
    private async Task<ErrorOr<PreparedRom>> VerifyAsync(ITempFile source, string filename,
        Func<ITempFile, CancellationToken, Task<ErrorOr<RvzInspection>>> verify, CancellationToken ct)
    {
        var inspected = await verify(source, ct);
        if (inspected.IsError)
            return inspected.Errors;
        var proof = inspected.Value.Verification;
        return new PreparedRom.WholeFile(source, Path.ChangeExtension(filename, ".iso"), proof.ContentSha256,
            new(proof.Sha1, proof.Md5, proof.Crc32, proof.ContentSize), Encoding, proof, inspected.Value.Container, encodingProfile: inspected.Value.EncodingProfile, inspection: inspected.Value.Inspection);
    }
    private sealed class Admission(RvzPreparationAdapter adapter, RvzVerifier.Admission slot) : IRomPreparationAdmission
    {
        public Task<ErrorOr<PreparedRom>> PrepareAsync(ITempFile source, string filename, CancellationToken ct = default) =>
            adapter.VerifyAsync(source, filename, slot.VerifyAsync, ct);
        public void Dispose() => slot.Dispose();
    }
}
