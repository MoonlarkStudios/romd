using Romd.Media.Rvz;
using Shouldly;
using Xunit;

namespace Romd.Media.Tests;

public sealed class RvzCanonicalRecipeTests
{
    [Fact]
    public void Recipe_ApprovedMeasurementDecision_HasVersionedIdentityAndParameters()
    {
        RvzCanonicalRecipe.Id.ShouldBe("rvz-canonical/v1");
        RvzCanonicalRecipe.Compression.ShouldBe(5u);
        RvzCanonicalRecipe.CompressionLevel.ShouldBe(19);
        RvzCanonicalRecipe.ChunkSize.ShouldBe(131_072);
    }

    [Theory]
    [InlineData(5u, 19, 131_072, true)]
    [InlineData(0u, 19, 131_072, false)]
    [InlineData(1u, 19, 131_072, false)]
    [InlineData(2u, 19, 131_072, false)]
    [InlineData(3u, 19, 131_072, false)]
    [InlineData(4u, 19, 131_072, false)]
    [InlineData(6u, 19, 131_072, false)]
    [InlineData(5u, -1, 131_072, false)]
    [InlineData(5u, 12, 131_072, false)]
    [InlineData(5u, 15, 131_072, false)]
    [InlineData(5u, 22, 131_072, false)]
    [InlineData(5u, 19, 0, false)]
    [InlineData(5u, 19, 65_536, false)]
    [InlineData(5u, 19, 524_288, false)]
    [InlineData(5u, 19, 1_048_576, false)]
    [InlineData(5u, 19, 2_097_152, false)]
    public void Matches_InspectedParameters_RequiresEveryRecipeParameter(uint compression,
        int compressionLevel, int chunkSize, bool expected)
    {
        RvzCanonicalRecipe.Matches(compression, compressionLevel, chunkSize).ShouldBe(expected);
    }

    [Theory]
    [InlineData(1u, false)]
    [InlineData(2u, true)]
    public void Matches_GameCubeAndWiiDescriptors_UsesTheSameRecipe(uint discType, bool hasPartitions)
    {
        var descriptor = new RvzDescriptor(0x01000000, discType, 5, 19, 131_072, hasPartitions);

        RvzCanonicalRecipe.Matches(descriptor.Compression, descriptor.CompressionLevel,
            descriptor.ChunkSize).ShouldBeTrue();
    }
}
