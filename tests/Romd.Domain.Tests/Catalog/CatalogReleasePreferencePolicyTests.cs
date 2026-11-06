using Romd.Domain.Catalog;
using Shouldly;
using Xunit;

namespace Romd.Domain.Tests.Catalog;

public sealed class CatalogReleasePreferencePolicyTests
{
    private readonly CatalogReleasePreferencePolicy _policy = new(["USA", "Europe", "Japan"]);

    [Fact]
    public void SelectDesired_PinTakesPrecedenceOverGlobalRanking()
    {
        var releases = new[]
        {
            Candidate(1, region: "USA", revision: "Rev 2"),
            Candidate(2, region: "Europe", revision: "Rev 1", inferior: true)
        };

        var selected = _policy.SelectDesired(releases, pinnedCatalogReleaseId: 2);

        selected!.CatalogReleaseId.ShouldBe(2);
    }

    [Fact]
    public void SelectDesired_CrossRegionBadDump_DumpQualityPrecedesRegionPriority()
    {
        var releases = new[]
        {
            Candidate(1, region: "Europe", inferior: false),
            Candidate(2, region: "usa", inferior: true)
        };

        _policy.SelectDesired(releases)!.CatalogReleaseId.ShouldBe(1);
    }

    [Fact]
    public void SelectDesired_CrossRegionNoDump_DumpQualityPrecedesRegionPriority()
    {
        var releases = new[]
        {
            Candidate(1, name: "Verified Europe", region: "Europe", inferior: false),
            Candidate(2, name: "Nodump USA", region: "USA", inferior: true)
        };

        _policy.SelectDesired(releases)!.CatalogReleaseId.ShouldBe(1);
    }

    [Fact]
    public void SelectDesired_SameRegionPrefersVerifiedDump()
    {
        var releases = new[]
        {
            Candidate(1, region: "USA", inferior: true),
            Candidate(2, region: "USA", inferior: false)
        };

        _policy.SelectDesired(releases)!.CatalogReleaseId.ShouldBe(2);
    }

    [Fact]
    public void SelectDesired_ParsesNewestMultiSegmentRevision()
    {
        var releases = new[]
        {
            Candidate(1, revision: "Rev 1.10"),
            Candidate(2, revision: "Rev 2.0")
        };

        _policy.SelectDesired(releases)!.CatalogReleaseId.ShouldBe(2);
    }

    [Fact]
    public void SelectDesired_EqualPreferenceUsesNameThenIdDeterministically()
    {
        var releases = new[]
        {
            Candidate(3, name: "Zulu"),
            Candidate(2, name: "Alpha"),
            Candidate(1, name: "alpha")
        };

        _policy.SelectDesired(releases)!.CatalogReleaseId.ShouldBe(1);
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("verified", false)]
    [InlineData("VeRiFiEd", false)]
    [InlineData("good", false)]
    [InlineData("GoOd", false)]
    [InlineData("baddump", true)]
    [InlineData("BaDdUmP", true)]
    [InlineData("nodump", true)]
    [InlineData("NoDuMp", true)]
    public void IsInferiorDumpStatus_StatusMatrix_IsCaseInsensitive(
        string? status,
        bool expectedInferior) =>
        CatalogReleasePreferencePolicy.IsInferiorDumpStatus(status).ShouldBe(expectedInferior);

    private static CatalogReleasePreferenceCandidate Candidate(
        int id,
        string name = "Release",
        string? region = "USA",
        string? revision = null,
        bool inferior = false) =>
        new(id, name, region, revision, inferior);
}
