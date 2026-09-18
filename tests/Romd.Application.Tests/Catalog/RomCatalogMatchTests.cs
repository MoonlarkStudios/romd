using Romd.Admin.Application.Catalog;
using Shouldly;
using Xunit;

namespace Romd.Application.Tests.Catalog;

/// <summary>
///     Pins the invariant the admin Import provenance ledger relies on: an ingested ROM item
///     carrying a platform but no matched titles can only come from a BIOS-only catalog match.
/// </summary>
public sealed class RomCatalogMatchTests
{
    [Fact]
    public void PrimaryPlatformId_TitleAndBiosMatches_PrefersTitlePlatform()
    {
        var match = new RomCatalogMatch([1], [10], [20]);

        match.PrimaryPlatformId.ShouldBe(10);
        match.HasCatalogMatch.ShouldBeTrue();
    }

    [Fact]
    public void PrimaryPlatformId_BiosOnlyMatch_FallsBackToBiosPlatform()
    {
        var match = new RomCatalogMatch([], [], [20]);

        match.PrimaryPlatformId.ShouldBe(20);
        match.HasCatalogMatch.ShouldBeTrue();
    }

    [Fact]
    public void PrimaryPlatformId_NoMatches_IsNull()
    {
        var match = new RomCatalogMatch([], [], []);

        match.PrimaryPlatformId.ShouldBeNull();
        match.HasCatalogMatch.ShouldBeFalse();
    }
}
