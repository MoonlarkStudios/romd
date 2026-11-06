using Romd.Admin.Application.Source.Rom;
using Romd.Domain.Source.Rom;
using Romd.PostgreSql.TestSupport;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Storage;

public sealed class RvzMediaProfileTests
{
    [Theory]
    [InlineData(1u, 5u, 19, 131072, false, RomRepresentationProfileClass.Canonical)]
    [InlineData(2u, 5u, 19, 131072, true, RomRepresentationProfileClass.Canonical)]
    [InlineData(1u, 5u, 5, 131072, false, RomRepresentationProfileClass.NonCanonical)]
    [InlineData(1u, 0u, 0, 32768, false, RomRepresentationProfileClass.NonCanonical)]
    [InlineData(1u, 2u, 19, 131072, false, RomRepresentationProfileClass.NonCanonical)]
    [InlineData(1u, 5u, 19, 262144, false, RomRepresentationProfileClass.NonCanonical)]
    [InlineData(1u, 5u, 19, 32768, false, RomRepresentationProfileClass.NonCanonical)]
    public void Classify_CapturedParameters_UsesCodecLevelAndBlock(uint discType, uint codec, int level, int block, bool wii, RomRepresentationProfileClass expected)
    {
        var observed = MediaFactFixtures.Rvz(131072, discType, codec, level, block, wii);
        TestRomMediaPolicies.Create().Classify(observed, discType == 1 ? RomIsoDiscKind.GameCube : RomIsoDiscKind.Wii).ShouldBe(expected);
    }

    [Fact]
    public void Classify_IdentityFacts_RemainUnknown() =>
        TestRomMediaPolicies.Create().Classify(RomContainerFacts.Identity.Instance, null).ShouldBe(RomRepresentationProfileClass.Unknown);
}
