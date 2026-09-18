namespace Romd.Domain.Libraries;

public enum UnknownMetadataPolicy
{
    Allow = 0,
    Hide = 1,
    NeedsReview = 2
}

public enum LibraryTitleSelectionMode
{
    Rules = 0,
    IncludeOnly = 1
}

public enum LibraryConfigurationState
{
    Valid = 0,
    Invalid = 1,
    RequiresMigration = 2
}

/// <summary>
///     Immutable configuration for a Library, controlling scope, access filters, and overrides.
/// </summary>
public sealed record LibraryConfiguration
{
    public static LibraryConfiguration InvalidFailClosedSentinel { get; } = new()
    {
        TitleSelectionMode = LibraryTitleSelectionMode.IncludeOnly,
        AllowedPlatformIds = [-1],
        ContentRatingPolicy = new ContentRatingPolicy
        {
            MaxMinimumAge = 0,
            UnknownRatingPolicy = UnknownMetadataPolicy.Hide
        },
        UnknownGenrePolicy = UnknownMetadataPolicy.Hide,
        ShowMissingGames = false
    };

    // Scope
    public LibraryTitleSelectionMode TitleSelectionMode { get; init; } = LibraryTitleSelectionMode.Rules;
    public IReadOnlyList<int> AllowedPlatformIds { get; init; } = [];
    public IReadOnlyList<int> ExcludedDatIds { get; init; } = [];

    // Filters
    public ContentRatingPolicy ContentRatingPolicy { get; init; } = new();
    public IReadOnlyList<string> AllowedGenres { get; init; } = [];
    public UnknownMetadataPolicy UnknownGenrePolicy { get; init; } = UnknownMetadataPolicy.Allow;

    // Display
    public bool ShowMissingGames { get; init; } = false;

    // Overrides
    public IReadOnlyList<int> IncludeTitleIds { get; init; } = [];
    public IReadOnlyList<int> ExcludeTitleIds { get; init; } = [];
}

public static class LibraryConfigurationValidator
{
    public static string? Validate(LibraryConfiguration config)
    {
        ArgumentNullException.ThrowIfNull(config);

        if (config.ContentRatingPolicy is null)
            return "Content rating policy is required.";

        var ratingPolicy = config.ContentRatingPolicy;
        if (ratingPolicy.BoardPreference is null)
            return "Rating board preference is required.";

        if (!Enum.IsDefined(ratingPolicy.BasisSelection))
            return "Rating basis selection is invalid.";

        if (!Enum.IsDefined(ratingPolicy.UnknownRatingPolicy))
            return "Unknown rating policy is invalid.";

        if (ratingPolicy.MaxMinimumAge is < 0)
            return "Maximum minimum age must be zero or greater.";

        if (ratingPolicy.BoardPreference.Count == 0)
            return "Rating board preference must not be empty.";

        if (ratingPolicy.BoardPreference.Any(board => !Enum.IsDefined(board)))
            return "Rating board preference contains an invalid board.";

        if (ratingPolicy.BoardPreference.Distinct().Count() != ratingPolicy.BoardPreference.Count)
            return "Rating board preference must not contain duplicates.";

        if (!Enum.IsDefined(config.UnknownGenrePolicy))
            return "Unknown genre policy is invalid.";

        if (!Enum.IsDefined(config.TitleSelectionMode))
            return "Title selection mode is invalid.";

        if (config.AllowedPlatformIds is null)
            return "Allowed platform IDs are required.";

        if (config.ExcludedDatIds is null)
            return "Excluded DAT IDs are required.";

        if (config.AllowedGenres is null)
            return "Allowed genres are required.";

        if (config.IncludeTitleIds is null)
            return "Included title IDs are required.";

        if (config.ExcludeTitleIds is null)
            return "Excluded title IDs are required.";

        if (config.AllowedPlatformIds.Any(id => id <= 0))
            return "Allowed platform IDs must be valid positive identifiers.";

        if (config.ExcludedDatIds.Any(id => id <= 0))
            return "Excluded DAT IDs must be valid positive identifiers.";

        if (config.IncludeTitleIds.Any(id => id <= 0))
            return "Included title IDs must be valid positive identifiers.";

        if (config.ExcludeTitleIds.Any(id => id <= 0))
            return "Excluded title IDs must be valid positive identifiers.";

        return null;
    }
}
