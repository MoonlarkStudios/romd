namespace Romd.Contracts.Management.Artwork;

public sealed record ArtworkAcquisition(string Role, string Status, string? SourceId, DateTimeOffset UpdatedAt);
public sealed record ArtworkAcquisitionState(IReadOnlyList<ArtworkAcquisition> Outcomes, Guid? ActiveJobId);
public sealed record ArtworkEnrichmentSettings(Guid Revision, bool FillPosters, bool FillHeroes, bool FillLogos,
    IReadOnlyList<string> Providers, bool FillBackdrops = true, bool ReviewBackdrops = false);
public sealed record UpdateArtworkEnrichmentSettingsRequest(bool FillPosters, bool FillHeroes, bool FillLogos,
    bool FillBackdrops = true, bool ReviewBackdrops = false);
