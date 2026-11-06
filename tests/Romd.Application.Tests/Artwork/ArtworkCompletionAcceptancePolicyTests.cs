using Romd.Admin.Application.Artwork;
using Romd.Application.Common.Ids;
using Romd.Contracts.Management.Artwork;
using Shouldly;
using Xunit;

namespace Romd.Application.Tests.Artwork;

public sealed class ArtworkCompletionAcceptancePolicyTests
{
    [Fact]
    public void Signature_SelectedSet_PreservesOriginalJsonBytesAndOrdinalEncodedIdOrdering()
    {
        var ids = new[] { IdCoder.Encode(20), IdCoder.Encode(3) }.Order(StringComparer.Ordinal).ToArray();
        var request = new ArtworkCompletionRequest(Guid.NewGuid(), [ids[1], ids[0], ids[1]], Filters: new(Query: "ignored"),
            RetryJobId: Guid.NewGuid(), RetryStatus: "ignored");

        var normalized = ArtworkCompletionIntent.Normalize(request);

        ArtworkCompletionIntent.Signature(normalized).ShouldBe($$"""{"RequestId":"00000000-0000-0000-0000-000000000000","TitleIds":["{{ids[0]}}","{{ids[1]}}"],"Scope":"Selected","Filters":null,"RetryJobId":null,"RetryStatus":null,"Mode":"Artwork"}""");
        normalized.RequestId.ShouldBe(request.RequestId);
        ArtworkCompletionIntent.Signature(ArtworkCompletionIntent.Normalize(request with { TitleIds = [ids[0], ids[1]] }))
            .ShouldBe(ArtworkCompletionIntent.Signature(normalized));
    }

    [Fact]
    public void Signature_FilterWithoutEnrichment_PreservesPreUpgradeOmissionAndPropertyOrder()
    {
        const string prior = """{"RequestId":"00000000-0000-0000-0000-000000000000","TitleIds":null,"Scope":"Filtered","Filters":{"Query":"Game","SystemKey":null,"Genre":null,"EnrichmentStatus":null,"Tracked":"all","ReleaseCompleteness":"all","ArtworkStatus":null},"RetryJobId":null,"RetryStatus":null,"Mode":"Enrich"}""";
        var normalized = ArtworkCompletionIntent.Normalize(new(Guid.NewGuid(), Scope: "Filtered", Filters: new(Query: "Game"), Mode: "Enrich"));

        ArtworkCompletionIntent.Signature(normalized).ShouldBe(prior);
    }

    [Fact]
    public void Normalize_OwnsRetainedInputs_WithoutReorderingOrDeduplicatingFilterFlags()
    {
        var ids = new List<string> { IdCoder.Encode(1) };
        var flags = new List<string> { "Blocked", "NeedsReview", "Blocked" };
        var selected = ArtworkCompletionIntent.Normalize(new(Guid.NewGuid(), ids));
        var filtered = ArtworkCompletionIntent.Normalize(new(Guid.NewGuid(), Scope: "Filtered", Filters: new(Enrichment: flags)));
        var signature = ArtworkCompletionIntent.Signature(filtered);
        ids.Clear();
        flags.Clear();

        selected.TitleIds.ShouldHaveSingleItem().ShouldBe(IdCoder.Encode(1));
        filtered.Filters!.Enrichment.ShouldBe(["Blocked", "NeedsReview", "Blocked"]);
        ArtworkCompletionIntent.Signature(filtered).ShouldBe(signature);
        Should.Throw<NotSupportedException>(() => ((IList<string>)selected.TitleIds!).Clear());
        Should.Throw<NotSupportedException>(() => ((IList<string>)filtered.Filters.Enrichment!).Clear());
    }

    [Theory]
    [InlineData("Artwork", "Artwork", false, false, true)]
    [InlineData("Enrich", "Artwork", false, false, true)]
    [InlineData("Artwork", "Enrich", false, false, false)]
    [InlineData("Enrich", "Enrich", false, false, true)]
    [InlineData("Enrich", "Enrich", true, false, false)]
    [InlineData("Enrich", "Enrich", true, true, true)]
    [InlineData("Enrich", "Enrich", false, true, true)]
    public void Plan_SharingRequiresCompatibleModeAndTracking(string existingMode, string requestedMode, bool ownerTracked, bool requestTracked, bool shares)
    {
        var owner = Guid.NewGuid();
        var scope = new ArtworkCompletionScopeSnapshot([new(1, "Title")], []);
        var request = new ArtworkCompletionRequest(Guid.NewGuid(), Scope: requestTracked ? "AllTracked" : "Selected", Mode: requestedMode);

        var item = ArtworkCompletionSharing.Plan(request, scope, [new(1, owner, existingMode, ownerTracked)]).Single();

        item.FollowUpJobId.ShouldBe(shares ? owner : null);
        item.RequireTracked.ShouldBe(requestTracked);
    }

    [Fact]
    public void Plan_PrefersFirstCompatibleOwnerAndRetainsSnapshotAndRetryTracking()
    {
        var preferred = Guid.NewGuid();
        var secondary = Guid.NewGuid();
        var titles = new List<ArtworkCompletionTitle> { new(2, "Snapshot name"), new(1, "Other") };
        var retryIds = new List<int> { 2 };
        var scope = new ArtworkCompletionScopeSnapshot(titles, retryIds);
        titles.Clear();
        retryIds.Clear();
        var request = new ArtworkCompletionRequest(Guid.NewGuid(), Scope: "Retry", Mode: "Enrich");
        var result = ArtworkCompletionSharing.Plan(request, scope,
            [new(2, Guid.NewGuid(), "Artwork", false), new(2, preferred, "Enrich", true), new(2, secondary, "Enrich", false)]);

        result.Select(item => item.TitleId).ShouldBe([2, 1]);
        result[0].ShouldBe(new ArtworkCompletionPlannedItem(2, "Snapshot name", true, preferred));
        result[1].FollowUpJobId.ShouldBeNull();
        result[1].RequireTracked.ShouldBeFalse();
        Should.Throw<NotSupportedException>(() => ((IList<ArtworkCompletionPlannedItem>)result).Clear());
    }
}
