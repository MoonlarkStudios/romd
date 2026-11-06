using System.Text;
using Moonlark.Libchdr;
using Romd.Media.PlayStation;
using Shouldly;
using Xunit;

namespace Romd.Media.Tests.PlayStation;

public sealed class ChdCdCueRegeneratorTests
{
    [Fact]
    public void Regenerate_RawTrack_UsesExactDatNameQuotedUtf8AndCrLfWithoutBom()
    {
        var track = Track(1, ChdCdTrackType.Mode2Raw, 300, 0, false, 0);
        byte[] cue = ChdCdCueRegenerator.Regenerate([new("Disc 日本語.bin", track)]);

        cue.ShouldBe(Encoding.UTF8.GetBytes("FILE \"Disc 日本語.bin\" BINARY\r\n  TRACK 01 MODE2/2352\r\n    INDEX 01 00:00:00\r\n"));
        ChdCdCue.Parse(cue).Files.Single().Name.ShouldBe("Disc 日本語.bin");
    }

    [Fact]
    public void Regenerate_SeparateTrackFiles_PreservesStoredAndVirtualPregapsAndPostgap()
    {
        var files = new ChdCdCueTrackFile[]
        {
            new("data.bin", Track(1, ChdCdTrackType.Mode1Raw, 300, 150, true, 0)),
            new("audio.bin", Track(2, ChdCdTrackType.Audio, 400, 75, false, 20))
        };

        byte[] cue = ChdCdCueRegenerator.Regenerate(files);

        cue.ShouldBe(Encoding.UTF8.GetBytes("FILE \"data.bin\" BINARY\r\n  TRACK 01 MODE1/2352\r\n    INDEX 00 00:00:00\r\n    INDEX 01 00:02:00\r\nFILE \"audio.bin\" BINARY\r\n  TRACK 02 AUDIO\r\n    PREGAP 00:01:00\r\n    INDEX 01 00:00:00\r\n    POSTGAP 00:00:20\r\n"));
        var parsed = ChdCdCue.Parse(cue);
        parsed.Files.Select(file => file.Tracks.Single()).ShouldBe([1, 2]);
        parsed.FirstTrackIndex01.ShouldBe(150);
    }

    [Theory]
    [InlineData("../disc.bin")]
    [InlineData("disc\".bin")]
    [InlineData("disc\n.bin")]
    [InlineData("disc.bin ")]
    [InlineData("")]
    public void Regenerate_UnsafeDatName_RejectsWithoutRenaming(string name) =>
        Should.Throw<InvalidDataException>(() => ChdCdCueRegenerator.Regenerate([new(name, Track(1, ChdCdTrackType.Mode2Raw, 300, 0, false, 0))]));

    [Theory]
    [InlineData("no-files")]
    [InlineData("too-many")]
    [InlineData("null-file")]
    [InlineData("null-track")]
    [InlineData("duplicate-name")]
    [InlineData("track-order")]
    [InlineData("empty-track")]
    [InlineData("partial-track")]
    [InlineData("stored-gap-without-bytes")]
    [InlineData("gap-consumes-track")]
    [InlineData("unbounded-gap")]
    [InlineData("unbounded-total")]
    public void Regenerate_InconsistentOrUnboundedTrackFiles_Rejects(string mutation)
    {
        var track = Track(1, ChdCdTrackType.Mode2Raw, 300, 0, false, 0);
        ChdCdCueTrackFile[] files = mutation switch
        {
            "no-files" => [],
            "too-many" => Enumerable.Range(1, 100).Select(n => new ChdCdCueTrackFile($"{n}.bin", track with { Number = (uint)n })).ToArray(),
            "null-file" => [null!],
            "null-track" => [new("disc.bin", null!)],
            "duplicate-name" => [new("DISC.bin", track), new("disc.bin", track with { Number = 2 })],
            "track-order" => [new("disc.bin", track with { Number = 2 })],
            "empty-track" => [new("disc.bin", track with { Frames = 0, BinBytes = 0 })],
            "partial-track" => [new("disc.bin", track with { BinBytes = track.BinBytes - 1 })],
            "stored-gap-without-bytes" => [new("disc.bin", track with { PregapStored = true })],
            "gap-consumes-track" => [new("disc.bin", track with { PregapStored = true, PregapFrames = 300 })],
            "unbounded-gap" => [new("disc.bin", track with { PostgapFrames = ChdCdLimits.MaximumFrames })],
            "unbounded-total" => [new("disc.bin", track with { Frames = ChdCdLimits.MaximumFrames + 1, BinBytes = (ChdCdLimits.MaximumFrames + 1L) * 2352 })],
            _ => throw new ArgumentException(mutation)
        };

        Should.Throw<InvalidDataException>(() => ChdCdCueRegenerator.Regenerate(files));
    }

    [Theory]
    [InlineData(ChdCdTrackType.Mode1)]
    [InlineData(ChdCdTrackType.Mode2FormMixed)]
    [InlineData((ChdCdTrackType)99)]
    public void Regenerate_UnsupportedSectorProjection_RefusesToInventRawMode(ChdCdTrackType type) =>
        Should.Throw<NotSupportedException>(() => ChdCdCueRegenerator.Regenerate([new("disc.bin", Track(1, type, 300, 0, false, 0))]));

    private static ChdCdTrackInspection Track(uint number, ChdCdTrackType type, uint frames, uint pregap, bool stored, uint postgap) =>
        new(number, type, 0, frames, 0, pregap, stored, postgap, frames * 2352L, new string('a', 64), new string('b', 40), new string('c', 32), "00000000");
}
