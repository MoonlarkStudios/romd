namespace Romd.Contracts.Management.Artwork;

public sealed record ArtworkAcquisitionDto(string Role, string Status, string? SourceId, DateTimeOffset UpdatedAt);
public sealed record ArtworkAcquisitionStateDto(IReadOnlyList<ArtworkAcquisitionDto> Outcomes, Guid? ActiveJobId);
public sealed record ArtworkEnrichmentSettingsDto(Guid Revision, bool FillPosters, bool FillHeroes, bool FillLogos,
    IReadOnlyList<string> Providers, bool FillBackdrops = true, bool ReviewBackdrops = true);
public sealed record UpdateArtworkEnrichmentSettingsRequest(Guid Revision, bool FillPosters, bool FillHeroes, bool FillLogos,
    bool FillBackdrops = true, bool ReviewBackdrops = true);
