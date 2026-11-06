namespace Romd.Media.Nintendo;

public enum NintendoIsoInspectionStatus { Eligible, Unsupported, Malformed, LimitExceeded }
public enum NintendoDiscKind { GameCube, Wii }

/// <summary>Structural eligibility facts; no ticket, key, signature-authenticity or content-integrity claim.</summary>
public sealed record NintendoIsoLayout(NintendoDiscKind Kind, long LogicalBytes, int WiiPartitionCount,
    int StandardKeyPartitions, int KoreanKeyPartitions);

public sealed record NintendoIsoInspectionResult(NintendoIsoInspectionStatus Status, NintendoIsoLayout? Layout = null);
