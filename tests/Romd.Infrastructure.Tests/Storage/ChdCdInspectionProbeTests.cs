using Romd.Media.Cd;
using Romd.Media.Probe;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Storage;

public sealed class ChdCdInspectionProbeTests
{
    private static ChdCdInspectionChecks.Configuration Valid => new("/native", "/fresh-data", "Independent track oracle",
        [new("/input.chd", [new(1, 2352, new string('a', 40), new string('b', 32), "00000000")])]);

    [Fact]
    public void ValidateConfiguration_ExplicitUnknownOracleAndZeroCrc_AreAccepted()
    {
        ChdCdInspectionChecks.ValidateConfiguration(Valid);
        ChdCdInspectionChecks.ValidateConfiguration(Valid with { Inputs = [new("/input.chd", null)] });
    }

    [Theory]
    [InlineData("empty-inputs")]
    [InlineData("too-many-inputs")]
    [InlineData("null-input")]
    [InlineData("empty-tracks")]
    [InlineData("too-many-tracks")]
    [InlineData("wrong-ordinal")]
    [InlineData("partial-sector")]
    [InlineData("zero-hash")]
    [InlineData("negative-size")]
    [InlineData("unbounded-provenance")]
    public void ValidateConfiguration_AmbiguousOrUnboundedOracle_RejectsBeforeStaging(string mutation)
    {
        var config = Valid;
        var track = config.Inputs[0].ExpectedTracks![0];
        config = mutation switch
        {
            "empty-inputs" => config with { Inputs = [] },
            "too-many-inputs" => config with { Inputs = Enumerable.Repeat(config.Inputs[0], 17).ToArray() },
            "null-input" => config with { Inputs = [null!] },
            "empty-tracks" => config with { Inputs = [new("/input.chd", [])] },
            "too-many-tracks" => config with { Inputs = [new("/input.chd", Enumerable.Repeat(track, ChdCdLimits.MaximumTracks + 1).ToArray())] },
            "wrong-ordinal" => config with { Inputs = [new("/input.chd", [track with { Number = 2 }])] },
            "partial-sector" => config with { Inputs = [new("/input.chd", [track with { BinBytes = 2351 }])] },
            "zero-hash" => config with { Inputs = [new("/input.chd", [track with { Sha1 = new string('0', 40) }])] },
            "negative-size" => config with { Inputs = [new("/input.chd", [track with { BinBytes = -2352 }])] },
            "unbounded-provenance" => config with { SourceDescription = new string('x', 4097) },
            _ => throw new ArgumentException(mutation)
        };

        Should.Throw<InvalidOperationException>(() => ChdCdInspectionChecks.ValidateConfiguration(config));
    }
}
