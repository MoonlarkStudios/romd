using Moonlark.Libchdr;
using Romd.Media.PlayStation;
using Shouldly;
using Xunit;

namespace Romd.Media.Tests.PlayStation;

public sealed class ChdDvdCanonicalRecipeTests
{
    private static readonly ChdHeaderFacts Canonical = new(10000, 16UL << 20, 16384, 2048, 1024, 0x7a737464, 0, 0, 0);

    [Fact]
    public void Recipe_FrozenDecision_HasVersionedIdentityAndParameters()
    {
        ChdDvdCanonicalRecipe.Id.ShouldBe("chd-dvd-canonical/v2");
        ChdDvdCanonicalRecipe.EncoderProfile.ShouldBe("dvd;unit=2048;hunk=16384;codec=zstd;processors=1");
        ChdDvdCanonicalRecipe.UnitBytes.ShouldBe(2048U);
        ChdDvdCanonicalRecipe.UnitBytes.ShouldBe(ChdDvdLimits.UnitBytes);
        ChdDvdCanonicalRecipe.HunkBytes.ShouldBe(16384U);
        ChdDvdCanonicalRecipe.Codec.ShouldBe(ChdCodec.Zstd);
        ((uint)ChdDvdCanonicalRecipe.Codec).ShouldBe(0x7a737464U);
        ChdDvdCanonicalRecipe.Processors.ShouldBe(1);
    }

    [Fact]
    public void ChdmanArguments_SourceAndOutput_EqualsFrozenCommandLine()
    {
        ChdDvdCanonicalRecipe.ChdmanArguments("/stage/source.iso", "/stage/output.chd").ShouldBe(
        [
            "createdvd", "-i", "/stage/source.iso", "-o", "/stage/output.chd", "-hs", "16384", "-c", "zstd", "-np", "1"
        ]);
    }

    [Theory]
    [InlineData(16UL << 20, 1024U)]
    [InlineData(133120UL, 9U)]
    [InlineData(2048UL, 1U)]
    public void Matches_CanonicalHeaderFacts_Accepts(ulong logicalBytes, uint hunkCount)
    {
        ChdDvdCanonicalRecipe.Matches(Canonical with { LogicalBytes = logicalBytes, HunkCount = hunkCount }).ShouldBeTrue();
    }

    [Theory]
    [InlineData("unit")]
    [InlineData("hunk")]
    [InlineData("hunk-count-short")]
    [InlineData("hunk-count-long")]
    [InlineData("codec0-historical-v1-zlib")]
    [InlineData("codec0-lzma")]
    [InlineData("codec0-none")]
    [InlineData("codec0-cd-zlib")]
    [InlineData("codec1")]
    [InlineData("codec2")]
    [InlineData("codec3")]
    public void Matches_DeviatedHeaderField_Rejects(string field)
    {
        ChdHeaderFacts deviated = field switch
        {
            "unit" => Canonical with { UnitBytes = 4096 },
            "hunk" => Canonical with { HunkBytes = 32768, HunkCount = 512 },
            "hunk-count-short" => Canonical with { HunkCount = 1023 },
            "hunk-count-long" => Canonical with { HunkCount = 1025 },
            "codec0-historical-v1-zlib" => Canonical with { Codec0 = (uint)ChdCodec.Zlib },
            "codec0-lzma" => Canonical with { Codec0 = (uint)ChdCodec.Lzma },
            "codec0-none" => Canonical with { Codec0 = (uint)ChdCodec.None },
            "codec0-cd-zlib" => Canonical with { Codec0 = (uint)ChdCodec.CdZlib },
            "codec1" => Canonical with { Codec1 = (uint)ChdCodec.Zstd },
            "codec2" => Canonical with { Codec2 = (uint)ChdCodec.Huffman },
            "codec3" => Canonical with { Codec3 = (uint)ChdCodec.Zstd },
            _ => throw new ArgumentOutOfRangeException(nameof(field))
        };

        ChdDvdCanonicalRecipe.Matches(deviated).ShouldBeFalse();
    }

    [Fact]
    public void Limits_FrozenBounds_ShareOnePreflightRecord()
    {
        ChdDvdLimits.UnitBytes.ShouldBe(2048U);
        ChdDvdLimits.MaximumArtifactBytes.ShouldBe(10L << 30);
        ChdDvdLimits.MaximumLogicalBytes.ShouldBe(10L << 30);
        ChdDvdLimits.MaximumHunkBytes.ShouldBe(1U << 20);
        ChdDvdLimits.MaximumHunkCount.ShouldBe(2_621_440U);
        ChdDvdLimits.Preflight.ShouldBe(new ChdHeaderPreflightLimits(10L << 30, 10UL << 30, 1U << 20, 2621440));
    }

    [Theory]
    [InlineData(ChdCodec.None, true, false)]
    [InlineData(ChdCodec.Zlib, true, false)]
    [InlineData(ChdCodec.Lzma, true, false)]
    [InlineData(ChdCodec.Huffman, true, false)]
    [InlineData(ChdCodec.Flac, true, false)]
    [InlineData(ChdCodec.Zstd, true, false)]
    [InlineData(ChdCodec.AvHuffman, false, false)]
    [InlineData(ChdCodec.CdZlib, false, true)]
    [InlineData(ChdCodec.CdLzma, false, true)]
    [InlineData(ChdCodec.CdFlac, false, true)]
    [InlineData(ChdCodec.CdZstd, false, true)]
    public void CodecClasses_DecoderCodec_ClassifiesDvdAndCdSeparately(ChdCodec codec, bool dvd, bool cd)
    {
        ChdDvdLimits.IsDvdCodec((uint)codec).ShouldBe(dvd);
        ChdDvdLimits.IsCdCodec((uint)codec).ShouldBe(cd);
    }

    [Fact]
    public void CodecClasses_UnknownFourCc_IsNeitherDvdNorCd()
    {
        ChdDvdLimits.IsDvdCodec(0x12345678).ShouldBeFalse();
        ChdDvdLimits.IsCdCodec(0x12345678).ShouldBeFalse();
    }
}
