using ErrorOr;
using Romd.Domain.Source.Rom;

namespace Romd.Admin.Application.Source.Rom;

public enum RomPs2DvdResolutionStatus
{
    Supported, PendingReference, PendingReferenceUnavailable, PendingConflictingReferences,
    UnsupportedBootLayout, UnsupportedMedia, UnsupportedSystem, UnsupportedFileMultiplicity
}

/// <summary>Only a positive resolution supplies current eligibility and its selected raw reference.</summary>
public sealed record RomPs2DvdEligibilityResolution
{
    internal RomPs2DvdEligibilityResolution(RomPs2DvdResolutionStatus status,
        RomIsoEligibility? eligibility = null, RomDiscMediaReferenceRecord? reference = null)
    {
        if (!Enum.IsDefined(status) || (status == RomPs2DvdResolutionStatus.Supported) != (eligibility is not null) ||
            eligibility is not null && (eligibility.DiscKind != RomIsoDiscKind.PlayStation2Dvd || reference is null))
            throw new ArgumentException("Only a supported DVD resolution can carry positive eligibility and its reference.");
        Status = status;
        Eligibility = eligibility;
        Reference = reference;
    }

    public RomPs2DvdResolutionStatus Status { get; }
    public RomIsoEligibility? Eligibility { get; }
    /// <summary>The exact selected raw provenance, including its trusted-import record identity.</summary>
    public RomDiscMediaReferenceRecord? Reference { get; }
}

/// <summary>Combines observed boot layout with independently trusted local DVD assertions.</summary>
/// <remarks>Stateless: unresolved, conflicting or unsupported results never create an immutable
/// Unsupported cache observation. A later reference import can resolve the same measurement/build.
/// The caller must bind the supplied boot observation to this logical measurement; boot facts have no hash.</remarks>
public sealed class RomPs2DvdEligibilityResolver
{
    public const string Adapter = "ps2-iso";
    public const int MaximumReferenceRecords = 128;
    private readonly IRomDiscMediaReferenceStore _references;

    public RomPs2DvdEligibilityResolver(IRomDiscMediaReferenceStore references)
    {
        ArgumentNullException.ThrowIfNull(references);
        _references = references;
    }

    public async Task<ErrorOr<RomPs2DvdEligibilityResolution>> ResolveAsync(RomLogicalIdentity measurement,
        RomPs2BootLayoutFacts? boot, DateTimeOffset inspectedAt, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(measurement);
        if (measurement.Sha256.IsEmpty || measurement.Sha1.IsEmpty || measurement.Md5.IsEmpty || measurement.Crc32.IsEmpty ||
            measurement.Size is <= 0 or > RomPs2BootLayoutFacts.MaximumLogicalBytes || measurement.Size % 2048 != 0 ||
            boot is not null && boot.LogicalBytes != measurement.Size)
            return RomDiscMediaReferenceErrors.InvalidMeasurement();
        if (boot is null)
            return new RomPs2DvdEligibilityResolution(RomPs2DvdResolutionStatus.UnsupportedBootLayout);

        var query = new RomDiscMediaReferenceQuery(measurement.Size, measurement.Sha1, measurement.Md5, measurement.Crc32);
        var found = await _references.FindAsync(query, MaximumReferenceRecords + 1, ct);
        ct.ThrowIfCancellationRequested();
        if (found.IsError)
        {
            if (found.Errors.Count == 1 && found.FirstError.Type == ErrorType.Failure &&
                found.FirstError.Code == RomDiscMediaReferenceErrors.UnavailableCode)
                return new RomPs2DvdEligibilityResolution(RomPs2DvdResolutionStatus.PendingReferenceUnavailable);
            return found.Errors;
        }
        return ResolveFacts(measurement, boot, inspectedAt, found.Value, ct);
    }

    /// <summary>Resolve already loaded current assertions using the same rules as the store-backed path.</summary>
    public static ErrorOr<RomPs2DvdEligibilityResolution> ResolveFacts(RomLogicalIdentity measurement, RomPs2BootLayoutFacts? boot,
        DateTimeOffset inspectedAt, IReadOnlyList<RomDiscMediaReferenceRecord> rows, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(measurement);
        if (measurement.Sha256.IsEmpty || measurement.Sha1.IsEmpty || measurement.Md5.IsEmpty || measurement.Crc32.IsEmpty ||
            measurement.Size is <= 0 or > RomPs2BootLayoutFacts.MaximumLogicalBytes || measurement.Size % 2048 != 0 ||
            boot is not null && boot.LogicalBytes != measurement.Size)
            return RomDiscMediaReferenceErrors.InvalidMeasurement();
        if (boot is null)
            return new RomPs2DvdEligibilityResolution(RomPs2DvdResolutionStatus.UnsupportedBootLayout);
        var query = new RomDiscMediaReferenceQuery(measurement.Size, measurement.Sha1, measurement.Md5, measurement.Crc32);
        if (rows is null)
            return RomDiscMediaReferenceErrors.InvalidStoreResult();
        int count = rows.Count;
        if (count < 0)
            return RomDiscMediaReferenceErrors.InvalidStoreResult();
        if (count > MaximumReferenceRecords)
            return RomDiscMediaReferenceErrors.ReferenceLimit();
        if (count == 0)
            return new RomPs2DvdEligibilityResolution(RomPs2DvdResolutionStatus.PendingReference);

        var records = new RomDiscMediaReferenceRecord[count];
        var identities = new Dictionary<Guid, RomDiscMediaReferenceRecord>();
        for (int index = 0; index < count; index++)
        {
            ct.ThrowIfCancellationRequested();
            var record = rows[index];
            if (record is null)
                return RomDiscMediaReferenceErrors.InvalidStoreResult();
            if (!record.Matches(query))
                return RomDiscMediaReferenceErrors.TupleMismatch();
            if (identities.TryGetValue(record.Id, out var sameIdentity) && sameIdentity != record)
                return RomDiscMediaReferenceErrors.InvalidStoreResult();
            identities[record.Id] = record;
            records[index] = record;
        }
        var first = records[0];
        if (records.Any(record => record.System != first.System || record.Media != first.Media || record.FileCount != first.FileCount))
            return new RomPs2DvdEligibilityResolution(RomPs2DvdResolutionStatus.PendingConflictingReferences);

        // All current assertions must agree before deterministic provenance selection.
        var selected = records.OrderBy(record => record.RawReferenceSha256)
            .ThenBy(record => record.ReferenceSource, StringComparer.Ordinal)
            .ThenBy(record => record.ParserProfile, StringComparer.Ordinal)
            .ThenBy(record => record.AcceptedSourceProvenance, StringComparer.Ordinal)
            .ThenBy(record => record.RawReferenceBytes).ThenBy(record => record.RetrievedAt.UtcTicks)
            .ThenBy(record => record.Id).First();
        ct.ThrowIfCancellationRequested();
        if (selected.System != RomDiscReferenceSystem.PlayStation2)
            return new RomPs2DvdEligibilityResolution(RomPs2DvdResolutionStatus.UnsupportedSystem, reference: selected);
        if (selected.Media is not (RomDiscReferenceMedia.Dvd5 or RomDiscReferenceMedia.Dvd9))
            return new RomPs2DvdEligibilityResolution(RomPs2DvdResolutionStatus.UnsupportedMedia, reference: selected);
        if (selected.FileCount != 1)
            return new RomPs2DvdEligibilityResolution(RomPs2DvdResolutionStatus.UnsupportedFileMultiplicity, reference: selected);

        var dvdReference = new RomDvdMediaReference(selected.Id, measurement.Sha256, measurement.Size,
            measurement.Sha1, measurement.Md5, measurement.Crc32,
            selected.Media == RomDiscReferenceMedia.Dvd5 ? RomDvdMediaKind.Dvd5 : RomDvdMediaKind.Dvd9,
            selected.ParserProfile, selected.RawReferenceSha256, selected.ReferenceSource);
        var details = new RomPs2DvdEligibilityFacts(measurement.Sha256, measurement.Size,
            measurement.Sha1, measurement.Md5, measurement.Crc32, boot, dvdReference);
        var eligibility = new RomIsoEligibility(measurement.Sha256, measurement.Size, Adapter,
            RomPs2DvdEligibilityFacts.ProfileVersion, boot.InspectorBuild, RomIsoEligibilityStatus.Supported,
            RomIsoDiscKind.PlayStation2Dvd, 0, 0, 0, inspectedAt, details);
        return new RomPs2DvdEligibilityResolution(RomPs2DvdResolutionStatus.Supported, eligibility, selected);
    }
}
