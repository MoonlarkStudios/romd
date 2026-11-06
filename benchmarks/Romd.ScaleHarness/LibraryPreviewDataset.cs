using Romd.Domain.Libraries;

namespace Romd.ScaleHarness;

/// <summary>Preview-specific deterministic shape; title indices are zero based.</summary>
public static class LibraryPreviewDataset
{
    public static LibraryConfiguration SavedConfiguration { get; } = new()
    {
        IncludeUntrackedTitles = true,
        ShowMissingGames = true,
        ContentRatingPolicy = new() { MaxMinimumAge = 10, UnknownRatingPolicy = UnknownMetadataPolicy.Allow }
    };

    public static LibraryConfiguration DraftConfiguration { get; } = new()
    {
        ShowMissingGames = true,
        ContentRatingPolicy = new() { MaxMinimumAge = 18, UnknownRatingPolicy = UnknownMetadataPolicy.Allow },
        ExcludedDatIds = Enumerable.Range(0, ScaleParameters.PlatformCount).Select(p => p * 2 + 1).ToArray()
    };

    public static bool HasCandidates(int titleIndex) => titleIndex % 10 != 9;
    public static bool IsOwned(int titleIndex) => titleIndex % 10 < 3;
    public static bool WasEligible(DeterministicDataset data, int titleIndex) => HasCandidates(titleIndex) &&
        (!data.TitleEnriched(titleIndex) || data.RatingAgeOfTitle(titleIndex) <= 10);
    public static bool IsEligible(DeterministicDataset data, int titleIndex) =>
        HasCandidates(titleIndex) && data.TitleTracked(titleIndex);

    public static bool MatchesView(DeterministicDataset data, int titleIndex, string view) => view switch
    {
        "matching" => IsEligible(data, titleIndex),
        "added" => IsEligible(data, titleIndex) && !WasEligible(data, titleIndex),
        "removed" => WasEligible(data, titleIndex) && !IsEligible(data, titleIndex),
        "excluded" => !IsEligible(data, titleIndex),
        "all" => true,
        _ => throw new ArgumentException("Unknown preview view.", nameof(view))
    };
}
