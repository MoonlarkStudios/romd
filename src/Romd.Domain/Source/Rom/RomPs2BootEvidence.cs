namespace Romd.Domain.Source.Rom;

/// <summary>One immutable observed boot layout bound to an exact original or CHD measurement, without DVD/reference authority.</summary>
/// <remarks>Trusted supervised callers supply the observed facts; this type validates their binding, not byte inspection.
/// Cached re-evaluation requires retained exact UTC ticks. Never infer them from legacy millisecond audit timestamps.
/// A CHD boot inspector is managed code; its build label is distinct from the native decoder tuple and worker MVID.</remarks>
public sealed record RomPs2BootEvidence
{
    private RomPs2BootEvidence(Guid id, RomVerification measurement, RomPs2BootLayoutFacts boot,
        long? measurementVerifiedAtUtcTicks, Guid? chdIntegrityId, RomChdIntegrity? chdIntegrity)
    {
        ArgumentNullException.ThrowIfNull(measurement);
        ArgumentNullException.ThrowIfNull(boot);
        if (id == Guid.Empty || id != measurement.Id || measurement.Crc32.IsEmpty || boot.LogicalBytes != measurement.ContentSize)
            throw new ArgumentException("Boot evidence requires its exact complete retained measurement and logical length.");
        if (measurementVerifiedAtUtcTicks is not { } ticks || ticks < 0 || ticks > DateTimeOffset.MaxValue.UtcTicks ||
            ticks != measurement.VerifiedAt.UtcTicks)
            throw new ArgumentException("Boot evidence requires retained exact measurement UTC ticks.", nameof(measurementVerifiedAtUtcTicks));

        if (measurement.Encoding == RomRepresentationEncoding.Identity)
        {
            if (chdIntegrityId is not null || chdIntegrity is not null)
                throw new ArgumentException("Original boot evidence cannot borrow CHD integrity.");
            _ = new RomPs2BootObservation(measurement, boot);
        }
        else if (measurement.Encoding == RomRepresentationEncoding.Chd)
        {
            if (chdIntegrityId is null || chdIntegrityId == Guid.Empty || chdIntegrity is null ||
                chdIntegrityId != chdIntegrity.Id || !chdIntegrity.MatchesMeasurement(measurement))
                throw new ArgumentException("CHD boot evidence requires its exact retained integrity and native identity.");
        }
        else
            throw new ArgumentException("Boot evidence describes only original or CHD measurements.", nameof(measurement));

        Id = id;
        Measurement = measurement;
        Boot = boot;
        MeasurementVerifiedAtUtcTicks = ticks;
        ChdIntegrity = chdIntegrity;
    }

    /// <summary>The measurement identity is also the one-boot-fact identity; no new evidence ID is allocated.</summary>
    public Guid Id { get; }
    public RomVerification Measurement { get; }
    public RomPs2BootLayoutFacts Boot { get; }
    public long MeasurementVerifiedAtUtcTicks { get; }
    public RomChdIntegrity? ChdIntegrity { get; }
    public Guid? ChdIntegrityId => ChdIntegrity?.Id;

    /// <summary>Capture an actual validated original-byte observation without introducing reference eligibility.</summary>
    public static RomPs2BootEvidence FromOriginal(RomPs2BootObservation observation)
    {
        ArgumentNullException.ThrowIfNull(observation);
        return new(observation.Measurement.Id, observation.Measurement, observation.Boot,
            observation.Measurement.VerifiedAt.UtcTicks, null, null);
    }

    /// <summary>Capture CHD logical boot facts with their complete independent measurement and exact container integrity.</summary>
    public static RomPs2BootEvidence FromChd(RomVerification measurement, RomPs2BootLayoutFacts boot, RomChdIntegrity chdIntegrity)
    {
        ArgumentNullException.ThrowIfNull(measurement);
        ArgumentNullException.ThrowIfNull(chdIntegrity);
        return new(measurement.Id, measurement, boot, measurement.VerifiedAt.UtcTicks, chdIntegrity.Id, chdIntegrity);
    }

    /// <summary>Restore the same measurement/boot keys and exact recorded ticks; absent original precision stays unqualified.</summary>
    public static RomPs2BootEvidence Rehydrate(Guid id, RomVerification measurement, RomPs2BootLayoutFacts boot,
        long? measurementVerifiedAtUtcTicks, Guid? chdIntegrityId, RomChdIntegrity? chdIntegrity) =>
        new(id, measurement, boot, measurementVerifiedAtUtcTicks, chdIntegrityId, chdIntegrity);

    /// <summary>Equality binds ID, complete quartet, artifact/encoding/profile/builds and the exact inspection instant.</summary>
    public bool MatchesMeasurement(RomVerification? measurement) => Measurement == measurement;
}
