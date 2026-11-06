using System.Text;
using Romd.Domain.Catalog;
using Shouldly;
using Xunit;

namespace Romd.Domain.Tests.Catalog;

public sealed class OriginalCueDescriptorTests
{
    [Fact]
    public void Parse_ExactRelativeReferences_PreservesAuthoredSpellingAndOriginalBytes()
    {
        byte[] bytes = Encoding.UTF8.GetBytes("REM provenance\r\nFILE \"Tracks/Track 01.bin\" BINARY\r\n TRACK 01 MODE2/2352\r\nFILE \"audio.wav\" WAVE\n");
        byte[] original = bytes.ToArray();
        var parsed = OriginalCueDescriptor.Parse(bytes, "disc/game.cue");
        parsed.Issue.ShouldBeNull();
        parsed.Dependencies.ShouldBe(["disc/Tracks/Track 01.bin", "disc/audio.wav"]);
        bytes.ShouldBe(original);
    }
    [Theory]
    [InlineData("../track.bin")]
    [InlineData("/track.bin")]
    [InlineData("C:/track.bin")]
    [InlineData("tracks\\track.bin")]
    [InlineData("CON.bin")]
    public void Parse_UnsafeReference_RejectsWithoutNormalization(string path) =>
        OriginalCueDescriptor.Parse(Encoding.UTF8.GetBytes($"FILE \"{path}\" BINARY\n"), "game.cue").Issue.ShouldNotBeNull();
    [Theory]
    [InlineData("Track.bin", "track.bin")]
    [InlineData("é.bin", "é.bin")]
    [InlineData("track", "track/one.bin")]
    [InlineData("game.cue", "track.bin")]
    public void Parse_PortableAliasesAndDescriptorSelfReference_Rejects(string first, string second) =>
        OriginalCueDescriptor.Parse(Encoding.UTF8.GetBytes($"FILE \"{first}\" BINARY\nFILE \"{second}\" BINARY\n"), "game.cue").Issue.ShouldNotBeNull();
    [Fact]
    public void Parse_NonUtf8OversizeMissingAndExcessReferences_ReportsReviewIssue()
    {
        OriginalCueDescriptor.Parse([0xff, 0xfe], "game.cue").Issue.ShouldBe("UnsupportedEncoding");
        OriginalCueDescriptor.Parse(new byte[OriginalCueDescriptor.MaximumBytes + 1], "game.cue").Issue.ShouldBe("UnsupportedDescriptor");
        OriginalCueDescriptor.Parse(Encoding.UTF8.GetBytes("REM no files"), "game.cue").Issue.ShouldBe("MissingFileReferences");
        OriginalCueDescriptor.Parse(Encoding.UTF8.GetBytes(string.Join('\n', Enumerable.Range(1, 101).Select(i => $"FILE {i}.bin BINARY"))), "game.cue").Issue.ShouldBe("TooManyDependencies");
    }
}
