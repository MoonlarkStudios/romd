namespace Romd.Domain.Source.Rom;

/// <summary>Observed structural partition counts; media kind and eligibility are computed separately.</summary>
public sealed record RomNintendoLayoutFacts : RomLayoutObservation
{
    public RomNintendoLayoutFacts(int partitions, int standardKeyPartitions, int koreanKeyPartitions)
    {
        if (partitions is < 0 or > 64 || standardKeyPartitions < 0 || koreanKeyPartitions < 0 ||
            (long)standardKeyPartitions + koreanKeyPartitions != partitions)
            throw new ArgumentException("Inspected partition counts must be complete and bounded.");
        Partitions = partitions;
        StandardKeyPartitions = standardKeyPartitions;
        KoreanKeyPartitions = koreanKeyPartitions;
    }
    public int Partitions { get; }
    public int StandardKeyPartitions { get; }
    public int KoreanKeyPartitions { get; }
    /// <summary>Nintendo discs are identified by a complete 32-byte header prefix.</summary>
    public override bool Describes(RomLogicalIdentity identity) => identity.Size >= 32;
}

/// <summary>Measured logical prefix and structural observations, independent of container, policy and reference authority.</summary>
public sealed class RomLogicalInspection
{
    private readonly byte[] _prefix;
    public RomLogicalInspection(Guid id, RomLogicalIdentity identity, string profile, string inspectorBuild, DateTimeOffset observedAt,
        ReadOnlySpan<byte> prefix, RomLayoutObservation? layout = null)
    {
        ArgumentNullException.ThrowIfNull(identity);
        RomFactLabels.Validate(profile, 128);
        RomFactLabels.Validate(inspectorBuild, 256);
        if (id == Guid.Empty || prefix.Length != Math.Min(identity.Size, 32) || layout is not null && !layout.Describes(identity))
            throw new ArgumentException("Inspection requires an exact logical prefix and consistent structural observations.");
        Id = id;
        Identity = identity;
        Profile = profile;
        InspectorBuild = inspectorBuild;
        ObservedAt = observedAt.ToUniversalTime();
        _prefix = prefix.ToArray();
        Layout = layout;
    }
    public Guid Id { get; }
    public RomLogicalIdentity Identity { get; }
    public string Profile { get; }
    public string InspectorBuild { get; }
    public DateTimeOffset ObservedAt { get; }
    public ReadOnlySpan<byte> Prefix => _prefix;
    /// <summary>The single structural layout this inspection observed, if any.</summary>
    public RomLayoutObservation? Layout { get; }
    public bool SameObservation(RomLogicalInspection other) => Identity == other.Identity && Profile == other.Profile &&
        InspectorBuild == other.InspectorBuild && Prefix.SequenceEqual(other.Prefix) && Layout == other.Layout;
}
