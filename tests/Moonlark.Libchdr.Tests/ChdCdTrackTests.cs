using System.Text;
using Xunit;

namespace Moonlark.Libchdr.Tests;

/// <summary>Checks strict allocation-free parsing against native metadata formats.</summary>
public sealed class ChdCdTrackTests
{
    /// <summary>Known MAME layout aliases retain their exact semantic data size.</summary>
    [Theory]
    [InlineData("MODE1", ChdCdTrackType.Mode1, 2048)]
    [InlineData("MODE1/2048", ChdCdTrackType.Mode1, 2048)]
    [InlineData("MODE1_RAW", ChdCdTrackType.Mode1Raw, 2352)]
    [InlineData("MODE1/2352", ChdCdTrackType.Mode1Raw, 2352)]
    [InlineData("MODE2", ChdCdTrackType.Mode2, 2336)]
    [InlineData("MODE2/2336", ChdCdTrackType.Mode2, 2336)]
    [InlineData("MODE2_FORM1", ChdCdTrackType.Mode2Form1, 2048)]
    [InlineData("MODE2/2048", ChdCdTrackType.Mode2Form1, 2048)]
    [InlineData("MODE2_FORM2", ChdCdTrackType.Mode2Form2, 2324)]
    [InlineData("MODE2/2324", ChdCdTrackType.Mode2Form2, 2324)]
    [InlineData("MODE2_FORM_MIX", ChdCdTrackType.Mode2FormMixed, 2336)]
    [InlineData("MODE2_RAW", ChdCdTrackType.Mode2Raw, 2352)]
    [InlineData("MODE2/2352", ChdCdTrackType.Mode2Raw, 2352)]
    [InlineData("CDI/2352", ChdCdTrackType.Mode2Raw, 2352)]
    [InlineData("AUDIO", ChdCdTrackType.Audio, 2352)]
    public void LegacyTrackLayoutsParse(string value, ChdCdTrackType type, int bytes)
    {
        byte[] payload = Encoding.ASCII.GetBytes($"TRACK:1 TYPE:{value} SUBTYPE:RW_RAW FRAMES:7\0");
        Assert.True(ChdCdTrack.TryParse(ChdMetadataTag.CdTrack, payload, out var track));
        Assert.Equal(1U, track.Number);
        Assert.Equal(type, track.Type);
        Assert.Equal(bytes, track.DataBytes);
        Assert.Equal(ChdCdSubcodeType.RwRaw, track.Subtype);
        Assert.Equal(96, track.SubcodeBytes);
        Assert.Equal(7U, track.Frames);
        Assert.Null(track.PregapType);
        Assert.False(track.PregapStored);
        Assert.Equal(ChdMetadataTag.CdTrack, track.MetadataTag);
    }

    /// <summary>A V prefix declares stored pregap frames; without it MAME ignores PGTYPE, so neither layout is reported.</summary>
    [Theory]
    [InlineData("VMODE2_FORM2", true)]
    [InlineData("MODE2_FORM2", false)]
    public void VersionTwoPreservesPregapSemantics(string pregapType, bool stored)
    {
        byte[] payload = Encoding.ASCII.GetBytes($"TRACK:2 TYPE:AUDIO SUBTYPE:NONE FRAMES:16 PREGAP:4 PGTYPE:{pregapType} PGSUB:RW POSTGAP:3");
        Assert.True(ChdCdTrack.TryParse(ChdMetadataTag.CdTrackV2, payload, out var track));
        Assert.Equal(ChdCdTrackType.Audio, track.Type);
        Assert.Equal(stored ? ChdCdTrackType.Mode2Form2 : null, track.PregapType);
        Assert.Equal(stored ? ChdCdSubcodeType.Rw : ChdCdSubcodeType.None, track.PregapSubtype);
        Assert.Equal(4U, track.PregapFrames);
        Assert.Equal(stored, track.PregapStored);
        Assert.Equal(3U, track.PostgapFrames);
        Assert.Equal(0U, track.PadFrames);
    }

    /// <summary>MAME ignores the V prefix of a zero-length pregap (chdman writes one when INDEX 00 equals INDEX 01), so nothing is stored.</summary>
    [Fact]
    public void ZeroLengthPregapIsNeverStored()
    {
        Assert.True(ChdCdTrack.TryParse(ChdMetadataTag.CdTrackV2,
            "TRACK:2 TYPE:AUDIO SUBTYPE:NONE FRAMES:16 PREGAP:0 PGTYPE:VAUDIO PGSUB:NONE POSTGAP:0"u8, out var track));
        Assert.False(track.PregapStored);
        Assert.Null(track.PregapType);
        Assert.Equal(0U, track.PregapFrames);
    }

    /// <summary>Both native GD tags retain padding without making CD image projection claims.</summary>
    [Theory]
    [InlineData("CHGD")]
    [InlineData("CHGT")]
    public void GdTagsParseTheirExplicitPadding(string tag)
    {
        var metadataTag = ChdMetadataTag.FromFourCC(tag);
        Assert.True(ChdCdTrack.TryParse(metadataTag,
            "TRACK:1 TYPE:MODE1 SUBTYPE:NONE FRAMES:100 PAD:7 PREGAP:0 PGTYPE:MODE1 PGSUB:NONE POSTGAP:0"u8, out var track));
        Assert.Equal(7U, track.PadFrames);
        Assert.Equal(metadataTag, track.MetadataTag);
    }

    /// <summary>Malformed or unknown text never produces a partial accepted track.</summary>
    [Theory]
    [InlineData("TRACK:0 TYPE:MODE1 SUBTYPE:NONE FRAMES:1")]
    [InlineData("TRACK:100 TYPE:MODE1 SUBTYPE:NONE FRAMES:1")]
    [InlineData("TRACK:1 TYPE:MODE1 SUBTYPE:NONE FRAMES:0")]
    [InlineData("TRACK:1 TYPE:MODE1 SUBTYPE:NONE FRAMES:-1")]
    [InlineData("TRACK:1 TYPE:MODE1 SUBTYPE:NONE FRAMES:+1")]
    [InlineData("TRACK:1 TYPE:MODE1 SUBTYPE:NONE FRAMES:4294967296")]
    [InlineData("TRACK:1 TYPE:UNKNOWN SUBTYPE:NONE FRAMES:1")]
    [InlineData("TRACK:1 TYPE:MODE1 SUBTYPE:UNKNOWN FRAMES:1")]
    [InlineData("TRACK:1 TYPE:MODE1 FRAMES:1 SUBTYPE:NONE")]
    [InlineData("TRACK:1 TYPE:MODE1 SUBTYPE:NONE FRAMES:1 EXTRA:0")]
    [InlineData("TRACK:1 TYPE:MODE1 SUBTYPE:NONE FRAMES:1\0garbage")]
    [InlineData("TRACK:1 TYPE:MODE1 SUBTYPE:NONE FRAMES:1\0\0")]
    [InlineData("TRACK:1 TYPE:MODE1 SUBTYPE:NONE FRAMES:1\n")]
    [InlineData(" TRACK:1 TYPE:MODE1 SUBTYPE:NONE FRAMES:1")]
    public void MalformedLegacyMetadataFailsClosed(string text)
    {
        Assert.False(ChdCdTrack.TryParse(ChdMetadataTag.CdTrack, Encoding.ASCII.GetBytes(text), out var track));
        Assert.Equal(default, track);
    }

    /// <summary>Missing fields, unknown gap layouts and stored pregaps outside the track are rejected.</summary>
    [Theory]
    [InlineData("TRACK:1 TYPE:AUDIO SUBTYPE:NONE FRAMES:3 PREGAP:4 PGTYPE:VAUDIO PGSUB:NONE POSTGAP:0")]
    [InlineData("TRACK:1 TYPE:AUDIO SUBTYPE:NONE FRAMES:3 PREGAP:1 PGTYPE:VUNKNOWN PGSUB:NONE POSTGAP:0")]
    [InlineData("TRACK:1 TYPE:AUDIO SUBTYPE:NONE FRAMES:3 PREGAP:1 PGTYPE:AUDIO PGSUB:UNKNOWN POSTGAP:0")]
    [InlineData("TRACK:1 TYPE:AUDIO SUBTYPE:NONE FRAMES:3 PREGAP:1 PGTYPE:AUDIO PGSUB:NONE")]
    [InlineData("TRACK:1 TYPE:AUDIO SUBTYPE:NONE FRAMES:3 PREGAP:1 PGTYPE:AUDIO PGSUB:NONE POSTGAP:4294967296")]
    public void MalformedGapMetadataFailsClosed(string text) =>
        Assert.False(ChdCdTrack.TryParse(ChdMetadataTag.CdTrackV2, Encoding.ASCII.GetBytes(text), out _));

    /// <summary>ASCII, size and tag bounds are checked before token processing.</summary>
    [Fact]
    public void MetadataBoundsAndUnknownTagFailClosed()
    {
        Assert.False(ChdCdTrack.TryParse(ChdMetadataTag.Dvd, "TRACK:1 TYPE:MODE1 SUBTYPE:NONE FRAMES:1"u8, out _));
        Assert.False(ChdCdTrack.TryParse(ChdMetadataTag.CdTrack, new byte[513], out _));
        Assert.False(ChdCdTrack.TryParse(ChdMetadataTag.CdTrack, [], out _));
        byte[] payload = "TRACK:1 TYPE:MODE1 SUBTYPE:NONE FRAMES:1"u8.ToArray();
        payload[1] = 0x80;
        Assert.False(ChdCdTrack.TryParse(ChdMetadataTag.CdTrack, payload, out _));
        Assert.True(ChdCdTrack.TryParse(ChdMetadataTag.CdTrack, "TRACK:99 TYPE:MODE1 SUBTYPE:NONE FRAMES:4294967295"u8, out var track));
        Assert.Equal(uint.MaxValue, track.Frames);
    }

    /// <summary>Warm typed metadata parsing allocates no managed bytes.</summary>
    [Fact]
    public void ParsingAllocatesNothingAfterWarmup()
    {
        ReadOnlySpan<byte> payload = "TRACK:1 TYPE:MODE1_RAW SUBTYPE:NONE FRAMES:16 PREGAP:0 PGTYPE:MODE1 PGSUB:NONE POSTGAP:0\0"u8;
        for (int index = 0; index < 100; index++)
            ChdCdTrack.TryParse(ChdMetadataTag.CdTrackV2, payload, out _);
        // One-time runtime transitions (such as tiering under load) may allocate once; a steady-state
        // allocation recurs in every window, so at least one window must allocate nothing.
        bool steady = false;
        for (int window = 0; window < 5 && !steady; window++)
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int index = 0; index < 1_000; index++)
                ChdCdTrack.TryParse(ChdMetadataTag.CdTrackV2, payload, out _);
            steady = GC.GetAllocatedBytesForCurrentThread() == before;
        }
        Assert.True(steady, "Parsing allocated in every steady-state window.");
    }
}
