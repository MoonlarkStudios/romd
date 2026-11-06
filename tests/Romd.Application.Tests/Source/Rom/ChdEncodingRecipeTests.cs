using Romd.Admin.Application.Source.Rom;
using Shouldly;
using Xunit;

namespace Romd.Application.Tests.Source.Rom;

public sealed class ChdEncodingRecipeTests
{
    private const string Observed = "chd-observed/v1;version=5;logical=131072;hunk=16384;unit=2048;count=8;codecs=7a737464,00000000,00000000,00000000;parent=none;metadata=chd-dvd-metadata/v1:1:44564420:1:1:0:c3ee3c942f2cbc35b3a4bec95ec700c2b9d21d5e90c7016b261504710490b406";

    [Theory]
    [InlineData(2048L, 1U)]
    [InlineData(131072L, 8U)]
    [InlineData(133120L, 9U)]
    [InlineData(10737418240L, 655360U)]
    public void Classify_CompleteObservedRecipeShape_ClassifiesWithoutEncoderProvenance(long logicalBytes, uint hunkCount)
    {
        string profile = Observed.Replace("logical=131072", $"logical={logicalBytes}", StringComparison.Ordinal)
            .Replace("count=8", $"count={hunkCount}", StringComparison.Ordinal);

        ChdEncodingRecipe.Classify(profile).ShouldBe(RomRepresentationProfileClass.Canonical);
    }

    [Fact]
    public void Classify_ReorderedCompleteFields_UsesActualParameters()
    {
        string[] fields = Observed.Split(';');
        string reordered = string.Join(';', fields.Take(1).Concat(fields.Skip(1).Reverse()));

        ChdEncodingRecipe.Classify(reordered).ShouldBe(RomRepresentationProfileClass.Canonical);
    }

    [Theory]
    [InlineData("hunk=16384;unit=2048;count=8", "hunk=8192;unit=2048;count=16")]
    [InlineData("hunk=16384;unit=2048;count=8", "hunk=32768;unit=2048;count=4")]
    [InlineData("7a737464,00000000,00000000,00000000", "7a6c6962,00000000,00000000,00000000")]
    [InlineData("7a737464,00000000,00000000,00000000", "6c7a6d61,00000000,00000000,00000000")]
    [InlineData("7a737464,00000000,00000000,00000000", "00000000,00000000,00000000,00000000")]
    [InlineData("7a737464,00000000,00000000,00000000", "7a737464,7a737464,00000000,00000000")]
    [InlineData("7a737464,00000000,00000000,00000000", "7a737464,00000000,7a737464,00000000")]
    [InlineData("7a737464,00000000,00000000,00000000", "7a737464,00000000,00000000,7a737464")]
    public void Classify_ReadableDifferentDvdParameters_RequiresConvergence(string before, string after)
    {
        ChdEncodingRecipe.Classify(Observed.Replace(before, after, StringComparison.Ordinal))
            .ShouldBe(RomRepresentationProfileClass.NonCanonical);
    }

    [Theory]
    [InlineData("version=5", "version=4")]
    [InlineData("logical=131072", "logical=131073")]
    [InlineData("logical=131072", "logical=10737420288")]
    [InlineData("hunk=16384", "hunk=16385")]
    [InlineData("unit=2048", "unit=2448")]
    [InlineData("count=8", "count=9")]
    [InlineData("count=8", "count=4294967296")]
    [InlineData("parent=none", "parent=present")]
    [InlineData("7a737464,00000000,00000000,00000000", "63647a73,00000000,00000000,00000000")]
    [InlineData("7a737464,00000000,00000000,00000000", "7a737464,00000000,00000000")]
    [InlineData("7a737464", "7A737464")]
    [InlineData("chd-dvd-metadata/v1:1:44564420:1:1:0:", "chd-dvd-metadata/v2:1:44564420:1:1:0:")]
    [InlineData("chd-dvd-metadata/v1:1:44564420:1:1:0:", "chd-dvd-metadata/v1:2:44564420:1:1:0:")]
    [InlineData("chd-dvd-metadata/v1:1:44564420:1:1:0:", "chd-dvd-metadata/v1:1:43445452:1:1:0:")]
    [InlineData("chd-dvd-metadata/v1:1:44564420:1:1:0:", "chd-dvd-metadata/v1:1:44564420:0:1:0:")]
    [InlineData("chd-dvd-metadata/v1:1:44564420:1:1:0:", "chd-dvd-metadata/v1:1:44564420:1:2:0:")]
    [InlineData("chd-dvd-metadata/v1:1:44564420:1:1:0:", "chd-dvd-metadata/v1:1:44564420:1:1:1:")]
    [InlineData("c3ee3c942f2cbc35b3a4bec95ec700c2b9d21d5e90c7016b261504710490b406", "a3ee3c942f2cbc35b3a4bec95ec700c2b9d21d5e90c7016b261504710490b406")]
    public void Classify_MalformedOrContradictoryCompleteTuple_Refuses(string before, string after)
    {
        ChdEncodingRecipe.Classify(Observed.Replace(before, after, StringComparison.Ordinal))
            .ShouldBe(RomRepresentationProfileClass.Unknown);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("extra")]
    [InlineData("duplicate")]
    [InlineData("control")]
    [InlineData("oversized")]
    [InlineData("canonical-id")]
    [InlineData("historical-canonical-id")]
    [InlineData("experimental-id")]
    [InlineData("encoder-profile")]
    [InlineData("historical-encoder-profile")]
    [InlineData("encoder-provenance")]
    public void Classify_IncompleteOrProvenanceOnlyLabels_NeverClassifiesCanonical(string mutation)
    {
        string profile = mutation switch
        {
            "missing" => Observed.Replace(";metadata=" + Observed.Split(";metadata=", StringSplitOptions.None)[1], "", StringComparison.Ordinal),
            "extra" => Observed + ";encoder=chd-dvd-canonical/v2",
            "duplicate" => Observed.Replace("unit=2048", "hunk=16384", StringComparison.Ordinal),
            "control" => Observed.Replace("parent=none", "parent=none\n", StringComparison.Ordinal),
            "oversized" => Observed + new string('x', 513),
            "canonical-id" => "chd-dvd-canonical/v2",
            "historical-canonical-id" => "chd-dvd-canonical/v1",
            "experimental-id" => "chd-dvd-experiment/v1",
            "encoder-profile" => "dvd;unit=2048;hunk=16384;codec=zstd;processors=1",
            "historical-encoder-profile" => "dvd;unit=2048;hunk=16384;codec=zlib;processors=1",
            "encoder-provenance" => "chdman/linux-x64/mame0289/source:" + new string('a', 64) + "/recipe:1",
            _ => throw new ArgumentOutOfRangeException(nameof(mutation))
        };

        ChdEncodingRecipe.Classify(profile).ShouldBe(RomRepresentationProfileClass.Unknown);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Classify_AbsentProfile_Refuses(string? profile) =>
        ChdEncodingRecipe.Classify(profile).ShouldBe(RomRepresentationProfileClass.Unknown);
}
