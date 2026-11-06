using ErrorOr;
using Romd.Admin.Application.Source.Rom;
using Romd.Admin.Application.Storage.Files;

namespace Romd.Infrastructure.Storage;

/// <summary>Original-media observations from one completed supervised inspection; local DVD resolution is scoped separately.</summary>
public sealed class RomOriginalMediaIdentifier : IRomOriginalMediaIdentifier
{
    private readonly RvzVerifier _verifier;
    public RomOriginalMediaIdentifier(RvzVerifier verifier) => _verifier = verifier;
    public async Task<IRomOriginalMediaIdentificationAdmission> AcquireAsync(CancellationToken ct = default) =>
        new Admission(await _verifier.AcquireAsync(ct));

    public async Task<ErrorOr<RomOriginalMediaIdentification>> IdentifyAsync(ITempFile source, CancellationToken ct = default)
    {
        using var admission = await AcquireAsync(ct);
        return await admission.IdentifyAsync(source, ct);
    }

    private sealed class Admission(RvzVerifier.Admission slot) : IRomOriginalMediaIdentificationAdmission
    {
        public async Task<ErrorOr<RomOriginalMediaIdentification>> IdentifyAsync(ITempFile source, CancellationToken ct = default)
        {
            return await slot.IdentifyOriginalIsoAsync(source, ct);
        }
        public void Dispose() => slot.Dispose();
    }
}
