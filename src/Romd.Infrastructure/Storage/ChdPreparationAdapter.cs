using System.Buffers.Binary;
using ErrorOr;
using Romd.Admin.Application.Source.Rom;
using Romd.Admin.Application.Source.Rom.Compressed;
using Romd.Admin.Application.Storage.Files;
using Romd.Domain.Source.Rom;

namespace Romd.Infrastructure.Storage;

/// <summary>Routes observed CHD geometry to whole-file DVD or unbound raw-CD inspection under one staged admission.</summary>
internal sealed class ChdPreparationAdapter : IRomPreparationAdapter
{
    private readonly Func<ChdVerifier> _dvd;
    private readonly Func<ChdCdImportInspector> _cd;
    public ChdPreparationAdapter(Func<ChdVerifier> dvd, Func<ChdCdImportInspector> cd)
    {
        ArgumentNullException.ThrowIfNull(dvd);
        ArgumentNullException.ThrowIfNull(cd);
        this._dvd = dvd;
        this._cd = cd;
    }
    public RomRepresentationEncoding Encoding => RomRepresentationEncoding.Chd;
    public ReadOnlySpan<byte> Signature => RomPreparationDetection.Signature(Encoding).Magic;
    public bool RequiresDatHashes(ReadOnlySpan<byte> prefix) => TryUnit(prefix, out uint unit) && unit == 2448;
    public bool Claims(ReadOnlySpan<byte> magic, string filename) => magic.StartsWith(Signature) ||
        RomPreparationDetection.Signature(Encoding).ClaimsFilename(filename);
    public async Task<IRomPreparationAdmission?> AdmitAsync(ReadOnlyMemory<byte> prefix, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        uint unit = RequireUnit(prefix.Span);
        return unit == 2448 ? new CdAdmission(await _cd().AcquireAsync(ct)) : new DvdAdmission(await _dvd().AcquireAsync(ct));
    }
    public async Task<ErrorOr<PreparedRom>> VerifyAsync(ITempFile source, string filename, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        byte[] prefix = new byte[124];
        uint unit;
        await using (var input = source.OpenRead())
        {
            int count = await input.ReadAtLeastAsync(prefix, prefix.Length, throwOnEndOfStream: false, ct);
            unit = RequireUnit(prefix.AsSpan(0, count));
        }
        return unit == 2448 ? await VerifyCdAsync(source, (artifact, token) => _cd().InspectAsync(artifact, token), ct)
            : await VerifyDvdAsync(source, filename, (artifact, token) => _dvd().VerifyAsync(artifact, token), ct);
    }
    private static async Task<ErrorOr<PreparedRom>> VerifyDvdAsync(ITempFile source, string filename,
        Func<ITempFile, CancellationToken, Task<ErrorOr<ChdVerifiedInspection>>> inspect, CancellationToken ct)
    {
        var result = await inspect(source, ct);
        if (result.IsError)
            return result.Errors;
        var facts = result.Value;
        var proof = facts.Verification;
        return new PreparedRom.WholeFile(source, Path.ChangeExtension(filename, ".iso"), proof.ContentSha256,
            new(proof.Sha1, proof.Md5, proof.Crc32, proof.ContentSize), RomRepresentationEncoding.Chd, proof, facts.Container,
            encodingProfile: proof.CapturedEncodingProfile, chdIntegrity: facts.Integrity, chdPs2Boot: facts.Ps2Boot, inspection: facts.Inspection);
    }
    private static async Task<ErrorOr<PreparedRom>> VerifyCdAsync(ITempFile source,
        Func<ITempFile, CancellationToken, Task<ErrorOr<RomCdInspection>>> inspect, CancellationToken ct)
    {
        var result = await inspect(source, ct);
        return result.IsError ? result.Errors : new PreparedRom.Cd(source, result.Value);
    }
    private static bool TryUnit(ReadOnlySpan<byte> prefix, out uint unit)
    {
        unit = 0;
        if (prefix.Length < 124 || !prefix.StartsWith(RomPreparationDetection.Signature(RomRepresentationEncoding.Chd).Magic) ||
            BinaryPrimitives.ReadUInt32BigEndian(prefix[8..]) != 124 || BinaryPrimitives.ReadUInt32BigEndian(prefix[12..]) != 5)
            return false;
        unit = BinaryPrimitives.ReadUInt32BigEndian(prefix[60..]);
        return unit is 2048 or 2448;
    }
    private static uint RequireUnit(ReadOnlySpan<byte> prefix) => TryUnit(prefix, out uint unit) ? unit
        : throw new InvalidDataException("CHD preparation requires a complete standalone v5 CD or DVD header.");
    private sealed class DvdAdmission(MediaInspectionAdmission<ChdVerifiedInspection> slot) : IRomPreparationAdmission
    {
        public Task<ErrorOr<PreparedRom>> PrepareAsync(ITempFile source, string filename, CancellationToken ct = default) =>
            VerifyDvdAsync(source, filename, slot.VerifyAsync, ct);
        public void Dispose() => slot.Dispose();
    }
    private sealed class CdAdmission(MediaInspectionAdmission<RomCdInspection> slot) : IRomPreparationAdmission
    {
        public Task<ErrorOr<PreparedRom>> PrepareAsync(ITempFile source, string filename, CancellationToken ct = default) => VerifyCdAsync(source, slot.VerifyAsync, ct);
        public void Dispose() => slot.Dispose();
    }
}
