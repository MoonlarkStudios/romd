using ErrorOr;
using Romd.Admin.Application.Storage.Files;
using Romd.Domain.Source.Rom;

namespace Romd.Admin.Application.Source.Rom;

/// <summary>Identifies original bytes within a supervised processing admission, independently of an output recipe.</summary>
public interface IRomOriginalMediaIdentifier
{
    Task<IRomOriginalMediaIdentificationAdmission> AcquireAsync(CancellationToken ct = default);
    Task<ErrorOr<RomOriginalMediaIdentification>> IdentifyAsync(ITempFile source, CancellationToken ct = default);
}

/// <summary>A single-use admission acquired before staging, or borrowed from the active supervised lease.</summary>
public interface IRomOriginalMediaIdentificationAdmission : IDisposable
{
    Task<ErrorOr<RomOriginalMediaIdentification>> IdentifyAsync(ITempFile source, CancellationToken ct = default);
}

/// <summary>Completed original measurement and optional observations; unresolved PS2 media has no eligibility cache.</summary>
public sealed record RomOriginalMediaIdentification
{
    public RomOriginalMediaIdentification(RomVerification verification, RomLogicalInspection inspection, RomIsoEligibility? eligibility,
        RomPs2BootObservation? ps2Observation = null)
    {
        ArgumentNullException.ThrowIfNull(verification);
        if (eligibility is not null && (eligibility.ContentSha256 != verification.ContentSha256 ||
            eligibility.ContentSize != verification.ContentSize))
            throw new ArgumentException("Original eligibility does not describe the measured bytes.", nameof(eligibility));
        if (ps2Observation is not null && (!ps2Observation.Matches(verification) || eligibility is not null))
            throw new ArgumentException("Unresolved PS2 boot facts must bind this exact original observation.", nameof(ps2Observation));
        if (inspection.Identity != new RomLogicalIdentity(verification.ContentSize, verification.ContentSha256, verification.Sha1, verification.Md5, verification.Crc32))
            throw new ArgumentException("Inspection must describe the complete measured original bytes.", nameof(inspection));
        Verification = verification;
        Inspection = inspection;
        Eligibility = eligibility;
        Ps2Observation = ps2Observation;
    }

    public RomVerification Verification { get; }
    public RomLogicalInspection Inspection { get; }
    public RomIsoEligibility? Eligibility { get; }
    public RomPs2BootObservation? Ps2Observation { get; }
}
