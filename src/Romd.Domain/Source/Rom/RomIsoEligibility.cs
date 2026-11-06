using Romd.Domain.Hashing;

namespace Romd.Domain.Source.Rom;

public enum RomIsoEligibilityStatus { Supported, Unsupported }
public enum RomIsoDiscKind { GameCube, Wii, PlayStation2Dvd, PlayStationPortableUmd }

/// <summary>An immutable structural observation, independent of ownership and current availability.</summary>
public sealed record RomIsoEligibility
{
    public RomIsoEligibility(Sha256 contentSha256, long contentSize, string adapter, string profile,
        string inspectorBuild, RomIsoEligibilityStatus status, RomIsoDiscKind? discKind,
        int wiiPartitionCount, int standardKeyPartitions, int koreanKeyPartitions, DateTimeOffset inspectedAt,
        RomPs2DvdEligibilityFacts? ps2DvdFacts = null)
        : this(Guid.NewGuid(), contentSha256, contentSize, adapter, profile, inspectorBuild, status, discKind,
            wiiPartitionCount, standardKeyPartitions, koreanKeyPartitions, inspectedAt, ps2DvdFacts)
    { }

    private RomIsoEligibility(Guid id, Sha256 contentSha256, long contentSize, string adapter, string profile,
        string inspectorBuild, RomIsoEligibilityStatus status, RomIsoDiscKind? discKind,
        int wiiPartitionCount, int standardKeyPartitions, int koreanKeyPartitions, DateTimeOffset inspectedAt,
        RomPs2DvdEligibilityFacts? ps2DvdFacts)
    {
        if (id == Guid.Empty || contentSha256.IsEmpty)
            throw new ArgumentException("Eligibility requires a measured content identity.");
        ArgumentOutOfRangeException.ThrowIfNegative(contentSize);
        ValidateLabel(adapter, 64);
        ValidateLabel(profile, 128);
        ValidateLabel(inspectorBuild, 256);
        if (!Enum.IsDefined(status) || discKind is { } kind && !Enum.IsDefined(kind))
            throw new ArgumentException("Eligibility requires a recognized status and disc kind.");
        bool emptyLayout = wiiPartitionCount == 0 && standardKeyPartitions == 0 && koreanKeyPartitions == 0;
        bool supportedPs2 = discKind == RomIsoDiscKind.PlayStation2Dvd && emptyLayout &&
            ps2DvdFacts is not null && ps2DvdFacts.ContentSha256 == contentSha256 && ps2DvdFacts.ContentSize == contentSize &&
            profile == RomPs2DvdEligibilityFacts.ProfileVersion && inspectorBuild == ps2DvdFacts.Boot.InspectorBuild;
        bool supportedLayout = discKind == RomIsoDiscKind.GameCube && emptyLayout ||
            discKind == RomIsoDiscKind.Wii && wiiPartitionCount is >= 1 and <= 64 &&
            standardKeyPartitions >= 0 && koreanKeyPartitions >= 0 &&
            (long)standardKeyPartitions + koreanKeyPartitions == wiiPartitionCount || supportedPs2;
        if (status == RomIsoEligibilityStatus.Unsupported ? discKind is not null || !emptyLayout : !supportedLayout)
            throw new ArgumentException("Eligibility layout does not match its observed status.");
        if (ps2DvdFacts is not null && (status != RomIsoEligibilityStatus.Supported || !supportedPs2))
            throw new ArgumentException("PS2 DVD facts require their exact supported eligibility observation.", nameof(ps2DvdFacts));
        Id = id;
        ContentSha256 = contentSha256;
        ContentSize = contentSize;
        Adapter = adapter;
        Profile = profile;
        InspectorBuild = inspectorBuild;
        Status = status;
        DiscKind = discKind;
        WiiPartitionCount = wiiPartitionCount;
        StandardKeyPartitions = standardKeyPartitions;
        KoreanKeyPartitions = koreanKeyPartitions;
        InspectedAt = inspectedAt.ToUniversalTime();
        Ps2DvdFacts = ps2DvdFacts;
    }

    public Guid Id { get; }
    public Sha256 ContentSha256 { get; }
    public long ContentSize { get; }
    public string Adapter { get; }
    public string Profile { get; }
    public string InspectorBuild { get; }
    public RomIsoEligibilityStatus Status { get; }
    public RomIsoDiscKind? DiscKind { get; }
    public int WiiPartitionCount { get; }
    public int StandardKeyPartitions { get; }
    public int KoreanKeyPartitions { get; }
    public DateTimeOffset InspectedAt { get; }
    public RomPs2DvdEligibilityFacts? Ps2DvdFacts { get; }

    public bool MatchesObservation(RomIsoEligibility other) =>
        ContentSha256 == other.ContentSha256 && ContentSize == other.ContentSize && Adapter == other.Adapter &&
        Profile == other.Profile && InspectorBuild == other.InspectorBuild && Status == other.Status && DiscKind == other.DiscKind &&
        WiiPartitionCount == other.WiiPartitionCount && StandardKeyPartitions == other.StandardKeyPartitions &&
        KoreanKeyPartitions == other.KoreanKeyPartitions && Ps2DvdFacts == other.Ps2DvdFacts;

    public static RomIsoEligibility Rehydrate(Guid id, Sha256 contentSha256, long contentSize, string adapter,
        string profile, string inspectorBuild, RomIsoEligibilityStatus status, RomIsoDiscKind? discKind,
        int wiiPartitionCount, int standardKeyPartitions, int koreanKeyPartitions, DateTimeOffset inspectedAt,
        RomPs2DvdEligibilityFacts? ps2DvdFacts = null) =>
        new(id, contentSha256, contentSize, adapter, profile, inspectorBuild, status, discKind,
            wiiPartitionCount, standardKeyPartitions, koreanKeyPartitions, inspectedAt, ps2DvdFacts);

    private static void ValidateLabel(string value, int limit)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (value.Length > limit)
            throw new ArgumentException("Eligibility identity is too long.");
        if (value.Any(char.IsControl))
            throw new ArgumentException("Eligibility identity cannot contain control characters.");
    }
}
