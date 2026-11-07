using Romd.Contracts.Management.Enrichment;
using Shouldly;
using State = Romd.Contracts.Management.Enrichment.EnrichmentIssueState;

namespace Romd.Infrastructure.Tests.Enrichment.Helpers;

/// <summary>
///     Independent oracle for the requirement contract (plan section 3, Phase 0 contract section B). It does not
///     reuse the production mapper, so a mapper defect cannot validate itself.
/// </summary>
internal static class EnrichmentRequirementInvariants
{
    private static readonly Dictionary<(string Group, State State), string[]> Allowed = new()
    {
        [("Identity", State.Satisfied)] = ["Trusted", "ProviderSuppressed"],
        [("Identity", State.NeedsReview)] = ["MatchDecisionRequired", "NoMatchFound"],
        [("Identity", State.Blocked)] = ["ProviderUnconfigured", "ProviderDisabled", "ProviderUnavailable"],
        [("Identity", State.Automatic)] = ["Discover"],
        [("Identity", State.Processing)] = ["Working"],
        [("Metadata", State.Satisfied)] = ["Present", "AcceptedOmission"],
        [("Metadata", State.NeedsReview)] = ["NotProvided", "ProviderSuppressed"],
        [("Metadata", State.Blocked)] = ["ProviderUnconfigured", "ProviderDisabled", "ProviderUnavailable", "AwaitingIdentity"],
        [("Metadata", State.Automatic)] = ["NotAttempted", "Stale"],
        [("Metadata", State.Processing)] = ["Working"],
        [("Refresh", State.Satisfied)] = ["Current"],
        [("Refresh", State.Automatic)] = ["Stale"],
        [("Refresh", State.Processing)] = ["Working"],
        [("Artwork", State.Satisfied)] = ["LeftEmpty", "Present", "NotRequested", "UnavailableFallback"],
        [("Artwork", State.NeedsReview)] = ["ArtworkDecisionRequired", "NothingUsable", "ProviderSuppressed"],
        [("Artwork", State.Blocked)] = ["ProviderUnconfigured", "ProviderDisabled", "ProviderUnavailable", "AwaitingIdentity", "AcquisitionFailed"],
        [("Artwork", State.Automatic)] = ["NotAttempted"],
        [("Artwork", State.Processing)] = ["Importing", "Searching"],
        [("Operation", State.Satisfied)] = ["Idle"],
        [("Operation", State.Processing)] = ["Working"]
    };

    private static readonly string[] KindOrder = ["Identity", "Metadata", "Artwork", "Operation"];
    private static readonly string[] MetadataSubjects = ["Description", "Genre", "ReleaseDate", "Refresh"];
    private static readonly string[] ArtworkSubjects = ["Poster", "Backdrop", "Hero", "Logo"];

    public static void AssertWellFormed(EnrichmentAssessment assessment)
    {
        var rows = assessment.Requirements;
        rows.Select(row => row.Key).ShouldBeUnique();
        rows.Select(row => row.Key).ShouldBe(rows.OrderBy(Rank).ThenBy(row => row.Subject, StringComparer.Ordinal).Select(row => row.Key));
        rows.Count(row => row.Kind == "Operation").ShouldBe(1);
        rows.Where(row => row.Kind == "Artwork").Select(row => row.Subject).ShouldAllBe(subject => ArtworkSubjects.Contains(subject));
        rows.Where(row => row.Kind == "Metadata").Select(row => row.Subject).ShouldAllBe(subject => MetadataSubjects.Contains(subject));
        foreach (var row in rows)
        {
            row.Key.ShouldBe($"{row.Kind}:{row.Subject}");
            var group = row.Kind == "Metadata" && row.Subject == "Refresh" ? "Refresh" : row.Kind;
            Allowed.TryGetValue((group, row.State), out var allowed).ShouldBeTrue($"{row.Key} cannot be {row.State}");
            if (row.State == State.Satisfied)
                row.Cause.ShouldBeNull(row.Key);
            else
                row.SatisfiedBy.ShouldBeNull(row.Key);
            var value = row.State == State.Satisfied ? row.SatisfiedBy?.ToString() : row.Cause?.ToString();
            allowed!.ShouldContain(value, $"{row.Key} {row.State}");
        }
        assessment.NeedsReview.ShouldBe(rows.Any(row => row.State == State.NeedsReview));
        assessment.CanEnrichAutomatically.ShouldBe(rows.Any(row => row.State == State.Automatic));
        assessment.HasActiveWork.ShouldBe(rows.Any(row => row.State == State.Processing));
        assessment.HasOperationalBlocker.ShouldBe(rows.Any(row => row.State == State.Blocked));
    }

    private static int Rank(EnrichmentRequirement row)
    {
        string[] subjects = row.Kind switch
        {
            "Identity" => ["igdb", "steamgriddb"],
            "Metadata" => MetadataSubjects,
            "Artwork" => ArtworkSubjects,
            _ => []
        };
        var subject = Array.IndexOf(subjects, row.Subject);
        return Array.IndexOf(KindOrder, row.Kind) * 100 + (subject < 0 ? subjects.Length : subject);
    }
}
