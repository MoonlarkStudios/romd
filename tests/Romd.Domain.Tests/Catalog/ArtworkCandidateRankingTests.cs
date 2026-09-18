using Romd.Domain.Catalog;
using Shouldly;
using Xunit;

namespace Romd.Domain.Tests.Catalog;

public sealed class ArtworkCandidateRankingTests
{
    [Fact]
    public void ArtworkRole_LogoAppendsWithoutRenumberingExistingRoles()
    {
        ((int)ArtworkRole.Poster).ShouldBe(1);
        ((int)ArtworkRole.Hero).ShouldBe(2);
        ((int)ArtworkRole.Logo).ShouldBe(3);
        ((int)ArtworkRole.Backdrop).ShouldBe(4);
    }

    [Theory]
    [InlineData(ArtworkRole.Poster, 600, 900, true)]
    [InlineData(ArtworkRole.Poster, 600, 600, true)]
    [InlineData(ArtworkRole.Poster, 900, 600, true)]
    [InlineData(ArtworkRole.Poster, 200, 300, false)]
    [InlineData(ArtworkRole.Hero, 1920, 620, true)]
    [InlineData(ArtworkRole.Hero, 1920, 1080, true)]
    [InlineData(ArtworkRole.Hero, 600, 900, false)]
    [InlineData(ArtworkRole.Hero, 800, 500, false)]
    [InlineData(ArtworkRole.Backdrop, 1920, 1080, true)]
    [InlineData(ArtworkRole.Backdrop, 1600, 1200, true)]
    [InlineData(ArtworkRole.Backdrop, 3840, 1240, false)]
    [InlineData(ArtworkRole.Backdrop, 960, 540, false)]
    [InlineData(ArtworkRole.Backdrop, 0, 0, false)]
    [InlineData(ArtworkRole.Logo, 128, 64, true)]
    [InlineData(ArtworkRole.Logo, 1000, 100, true)]
    [InlineData(ArtworkRole.Logo, 100, 1000, false)]
    [InlineData(ArtworkRole.Logo, 1000, 50, false)]
    public void IsSuitable_RespectsRoleWithoutForcingPortraitCovers(ArtworkRole role, int width, int height, bool expected) =>
        ArtworkCandidateRanking.IsSuitable(role, width, height).ShouldBe(expected);

    [Fact]
    public void CropCost_PrefersWideHeroWithoutPenalizingCoverPackaging()
    {
        ArtworkCandidateRanking.CropCost(ArtworkRole.Hero, 1920, 620)
            .ShouldBeLessThan(ArtworkCandidateRanking.CropCost(ArtworkRole.Hero, 1920, 1080));
        ArtworkCandidateRanking.CropCost(ArtworkRole.Poster, 900, 600).ShouldBe(0);
        ArtworkCandidateRanking.CropCost(ArtworkRole.Logo, 2400, 300).ShouldBe(0);
    }
}
