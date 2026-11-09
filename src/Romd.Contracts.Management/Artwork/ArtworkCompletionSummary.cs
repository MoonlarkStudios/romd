namespace Romd.Contracts.Management.Artwork;

/// <summary>Expensive current/recorded artwork projections, separate from the canonical job row.</summary>
public sealed record ArtworkCompletionSummary(IReadOnlyList<ArtworkRoleCount> Roles,
    int Review, int Failed, int Unfinished, int Working);
