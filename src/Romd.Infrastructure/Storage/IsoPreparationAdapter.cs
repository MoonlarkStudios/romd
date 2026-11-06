using System.Buffers.Binary;
using ErrorOr;
using Romd.Admin.Application.Source.Rom;
using Romd.Admin.Application.Storage.Files;
using Romd.Domain.Source.Rom;

namespace Romd.Infrastructure.Storage;

internal sealed class IsoPreparationAdapter : IRomPreparationAdapter
{
    private readonly Func<IRomOriginalMediaIdentifier> _identifier;
    public IsoPreparationAdapter(Func<IRomOriginalMediaIdentifier> identifier) => _identifier = identifier;
    public RomRepresentationEncoding Encoding => RomRepresentationEncoding.Identity;
    public ReadOnlySpan<byte> Signature => ReadOnlySpan<byte>.Empty;
    public bool RequiresDatHashes(ReadOnlySpan<byte> prefix) => false;
    public bool Claims(ReadOnlySpan<byte> magic, string filename) =>
        RomPreparationDetection.IsIsoCandidate(filename) && HasNintendoMagic(magic) ||
        Path.GetExtension(filename).Equals(".iso", StringComparison.OrdinalIgnoreCase) && HasIso9660Descriptor(magic);

    internal static bool HasNintendoMagic(ReadOnlySpan<byte> magic) =>
        magic.Length >= 0x1C && BinaryPrimitives.ReadUInt32BigEndian(magic[0x18..]) == 0x5D1C9EA3 ||
        magic.Length >= 0x20 && BinaryPrimitives.ReadUInt32BigEndian(magic[0x1C..]) == 0xC2339F3D;

    private static bool HasIso9660Descriptor(ReadOnlySpan<byte> magic)
    {
        const int offset = 16 * 2048;
        return magic.Length >= RomPreparationDetection.MaximumPrefixBytes && magic[offset] is <= 3 &&
            magic.Slice(offset + 1, 5).SequenceEqual("CD001"u8) && magic[offset + 6] == 1;
    }
    public async Task<IRomPreparationAdmission?> AdmitAsync(ReadOnlyMemory<byte> magic, CancellationToken ct) =>
        new Admission(this, await _identifier().AcquireAsync(ct));
    public Task<ErrorOr<PreparedRom>> VerifyAsync(ITempFile source, string filename, CancellationToken ct) =>
        VerifyAsync(source, filename, (input, token) => _identifier().IdentifyAsync(input, token), ct);
    private async Task<ErrorOr<PreparedRom>> VerifyAsync(ITempFile source, string filename,
        Func<ITempFile, CancellationToken, Task<ErrorOr<RomOriginalMediaIdentification>>> inspect, CancellationToken ct)
    {
        var inspected = await inspect(source, ct);
        if (inspected.IsError)
            return inspected.Errors;
        var proof = inspected.Value.Verification;
        string logicalFilename = inspected.Value.Eligibility?.Status == RomIsoEligibilityStatus.Supported
            ? Path.ChangeExtension(filename, ".iso") : filename;
        return new PreparedRom.WholeFile(source, logicalFilename, proof.ContentSha256,
            new(proof.Sha1, proof.Md5, proof.Crc32, proof.ContentSize), Encoding, proof, RomContainerFacts.Identity.Instance,
            encodingProfile: "identity/v1", eligibility: inspected.Value.Eligibility, ps2Observation: inspected.Value.Ps2Observation, inspection: inspected.Value.Inspection);
    }
    private sealed class Admission(IsoPreparationAdapter adapter, IRomOriginalMediaIdentificationAdmission slot) : IRomPreparationAdmission
    {
        public Task<ErrorOr<PreparedRom>> PrepareAsync(ITempFile source, string filename, CancellationToken ct = default) =>
            adapter.VerifyAsync(source, filename, slot.IdentifyAsync, ct);
        public void Dispose() => slot.Dispose();
    }
}
