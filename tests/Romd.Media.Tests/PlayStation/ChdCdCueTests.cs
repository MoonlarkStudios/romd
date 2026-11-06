using System.Text;
using Romd.Media.PlayStation;
using Romd.Media.TestSupport;
using Shouldly;
using Xunit;

namespace Romd.Media.Tests.PlayStation;

public sealed class ChdCdCueTests
{
    [Theory]
    [InlineData("single", 1)]
    [InlineData("multi", 3)]
    [InlineData("partial", 2)]
    public void Parse_OriginalFixture_MapsWholeFilesWithoutTreatingEachTrackAsAFile(string shape, int count)
    {
        var cue = ChdCdCue.Parse(File.ReadAllBytes(NativeTestBuilds.LibchdrFixture($"cd-edge-{shape}.cue")));
        cue.Files.Count.ShouldBe(count);
        if (shape == "single")
            cue.Files[0].Tracks.ShouldBe([1, 2, 3]);
        var paths = cue.Files.ToDictionary(f => f.Name, f => "/staged/" + f.Name);
        string rendered = Encoding.UTF8.GetString(cue.RenderToolCue(paths, "/staged/tool.cue"));
        foreach (var file in cue.Files)
            rendered.ShouldContain($"FILE \"{file.Name}\" BINARY");
        string nested = Encoding.UTF8.GetString(cue.RenderToolCue(paths, "/staged/descriptors/tool.cue"));
        foreach (var file in cue.Files)
            nested.ShouldContain($"FILE \"../{file.Name}\" BINARY");
    }

    [Theory]
    [InlineData("FILE \"../disc.bin\" BINARY\nTRACK 01 AUDIO\nINDEX 01 00:00:00")]
    [InlineData("FILE \"/disc.bin\" BINARY\nTRACK 01 AUDIO\nINDEX 01 00:00:00")]
    [InlineData("FILE \"C:\\disc.bin\" BINARY\nTRACK 01 AUDIO\nINDEX 01 00:00:00")]
    [InlineData("FILE \"disc.wav\" WAVE\nTRACK 01 AUDIO\nINDEX 01 00:00:00")]
    [InlineData("FILE \"disc.bin\" BINARY\nTRACK 01 MODE1/2048\nINDEX 01 00:00:00")]
    [InlineData("FILE \"disc.bin\" BINARY\nTRACK 02 AUDIO\nINDEX 01 00:00:00")]
    [InlineData("FILE \"disc.bin\" BINARY\nTRACK 01 AUDIO")]
    [InlineData("FILE \"disc.bin\" BINARY\nTRACK 01 AUDIO\nINDEX 01 00:60:00")]
    [InlineData("FILE \"disc.bin\" BINARY\nTRACK 01 AUDIO\nINDEX 01 00:00:75")]
    [InlineData("FILE \"disc.bin\" BINARY\nTRACK 01 AUDIO\nINDEX 01 00:00:00\nINDEX 00 00:00:00")]
    [InlineData("FILE \"disc.bin\" BINARY\nTRACK 01 AUDIO\nINDEX 01 00:00:00\nFILE \"DISC.bin\" BINARY")]
    [InlineData("FILE \"disc.bin\" BINARY\nTRACK 01 AUDIO\nINDEX 01 00:00:00\nCDTEXTFILE \"secret\"")]
    public void Parse_UnsupportedOrUnsafeCue_Rejects(string text) =>
        Should.Throw<InvalidDataException>(() => ChdCdCue.Parse(Encoding.UTF8.GetBytes(text)));

    [Fact]
    public void Parse_OversizedOrInvalidUtf8_Rejects()
    {
        Should.Throw<InvalidDataException>(() => ChdCdCue.Parse(new byte[ChdCdCue.MaximumBytes + 1]));
        Should.Throw<DecoderFallbackException>(() => ChdCdCue.Parse(new byte[] { 0xff }));
    }
    [Theory]
    [InlineData("missing")]
    [InlineData("absolute-injection")]
    [InlineData("extra")]
    public void RenderToolCue_RequiresExactSafeStagedReferences(string mutation)
    {
        var cue = ChdCdCue.Parse("FILE \"disc.bin\" BINARY\nTRACK 01 AUDIO\nINDEX 01 00:00:00"u8);
        Dictionary<string, string> paths = new() { ["disc.bin"] = "/staged/bin" };
        if (mutation == "missing")
            paths.Clear();
        else if (mutation == "extra")
            paths["other.bin"] = "/staged/other";
        else
            paths["disc.bin"] = "/staged/evil\"\nFILE \"other";
        Should.Throw<InvalidDataException>(() => cue.RenderToolCue(paths, "/staged/tool.cue"));
    }
}
