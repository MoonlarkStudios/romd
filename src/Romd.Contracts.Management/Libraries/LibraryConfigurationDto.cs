namespace Romd.Contracts.Management.Libraries;

public sealed record LibraryConfigurationDto
{
    public LibraryTitleSelectionMode TitleSelectionMode { get; init; } = LibraryTitleSelectionMode.Rules;
    public IReadOnlyList<string> AllowedSystemKeys { get; init; } = [];
    public IReadOnlyList<string> ExcludedDatIds { get; init; } = [];
    public ContentRatingPolicyDto? ContentRatingPolicy { get; init; } = new();
    public IReadOnlyList<string> AllowedGenres { get; init; } = [];
    public UnknownMetadataPolicy UnknownGenrePolicy { get; init; } = UnknownMetadataPolicy.Allow;
    public bool ShowMissingGames { get; init; }
    public IReadOnlyList<string> IncludeTitleIds { get; init; } = [];
    public IReadOnlyList<string> ExcludeTitleIds { get; init; } = [];
}
