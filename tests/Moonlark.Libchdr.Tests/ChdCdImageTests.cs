using System.Buffers.Binary;
using System.Text;
using Xunit;

namespace Moonlark.Libchdr.Tests;

/// <summary>Checks stored CD projection with pinned-tool fixtures and bounded uncompressed synthetic containers.</summary>
public sealed class ChdCdImageTests
{
    /// <summary>Every pinned CD codec preserves stored frames and exposes stored audio without implicit swapping.</summary>
    [Theory]
    [InlineData("cdlz")]
    [InlineData("cdzl")]
    [InlineData("cdfl")]
    [InlineData("cdzs")]
    public void PinnedCdFixturesPreserveExactFramesAndAudio(string codec)
    {
        using ChdFile file = NativeTestEnvironment.OpenFixture($"cd-{codec}.chd");
        using ChdCdImage image = ChdCdImage.Open(file, leaveOpen: true);
        byte[] expected = File.ReadAllBytes(NativeTestEnvironment.Fixture($"cd-{codec}.logical"));
        byte[] source = File.ReadAllBytes(NativeTestEnvironment.Fixture("cd-source.bin"));
        Assert.Equal(2, image.Tracks.Length);
        Assert.Equal(ChdCdTrackType.Mode1Raw, image.Tracks[0].Type);
        Assert.Equal(ChdCdTrackType.Audio, image.Tracks[1].Type);
        Assert.Equal(4U, image.Tracks[1].PregapFrames);
        Assert.True(image.Tracks[1].PregapStored);
        Assert.Equal(32UL, image.FrameCount);
        Assert.Equal(new ChdCdTrackLayout(1, 0, 16, 0), image.TrackLayouts[0]);
        Assert.Equal(new ChdCdTrackLayout(2, 16, 16, 0), image.TrackLayouts[1]);
        Assert.Equal(0U, image.TrackLayouts[1].AlignmentFrames);
        Assert.Equal(16UL * ChdCdImage.FrameBytes, image.TrackLayouts[1].ByteOffset);
        Span<byte> frame = stackalloc byte[2448];
        for (ulong index = 0; index < 32; index++)
        {
            image.ReadFrame(index, frame);
            Assert.True(frame.SequenceEqual(expected.AsSpan((int)index * 2448, 2448)));
        }
        Assert.Equal(2352, image.ReadSector(1, 0, ChdCdSectorFormat.Raw2352, frame));
        Assert.True(frame[..2352].SequenceEqual(source.AsSpan(0, 2352)));
        Assert.Equal(2352, image.ReadSector(2, 0, ChdCdSectorFormat.Raw2352, frame));
        Assert.True(frame[..2352].SequenceEqual(expected.AsSpan(16 * 2448, 2352)));
        ChdCdImage.SwapAudioSamples16(frame[..2352]);
        Assert.True(frame[..2352].SequenceEqual(source.AsSpan(16 * 2352, 2352)));
        Assert.Throws<NotSupportedException>(() => image.ReadSector(1, 0, ChdCdSectorFormat.Subcode96, new byte[96]));
    }

    /// <summary>Nonzero raw subcode is exposed separately and full frames remain byte exact.</summary>
    [Fact]
    public void PinnedSubcodeFixturePreservesSemanticSubcode()
    {
        using ChdFile file = NativeTestEnvironment.OpenFixture("cd-subcode.chd");
        using ChdCdImage image = ChdCdImage.Open(file, leaveOpen: true);
        byte[] expected = File.ReadAllBytes(NativeTestEnvironment.Fixture("cd-subcode.logical"));
        Assert.Single(image.Tracks.ToArray());
        Assert.Equal(ChdCdSubcodeType.RwRaw, image.Tracks[0].Subtype);
        Span<byte> frame = stackalloc byte[2448];
        Span<byte> subcode = stackalloc byte[96];
        for (uint index = 0; index < 16; index++)
        {
            image.ReadFrame(index, frame);
            Assert.True(frame.SequenceEqual(expected.AsSpan((int)index * 2448, 2448)));
            Assert.Equal(96, image.ReadSector(1, index, ChdCdSectorFormat.Subcode96, subcode));
            Assert.True(subcode.SequenceEqual(expected.AsSpan((int)index * 2448 + 2352, 96)));
        }
    }

    /// <summary>Track starts round up to four frames, track-local bounds exclude padding, and stored pregap frames use the track layout as MAME reads them.</summary>
    [Fact]
    public void PaddingAndStoredPregapUseTheTrackLayout()
    {
        byte[] logical = Pattern(8);
        using ChdFile file = Open(logical,
            "TRACK:1 TYPE:MODE1_RAW SUBTYPE:NONE FRAMES:3 PREGAP:0 PGTYPE:MODE1 PGSUB:NONE POSTGAP:0",
            "TRACK:2 TYPE:AUDIO SUBTYPE:NONE FRAMES:2 PREGAP:1 PGTYPE:VAUDIO PGSUB:NONE POSTGAP:7");
        using ChdCdImage image = ChdCdImage.Open(file, leaveOpen: true);
        Assert.Equal(8UL, image.FrameCount);
        Assert.Equal(new ChdCdTrackLayout(1, 0, 3, 1), image.TrackLayouts[0]);
        Assert.Equal(new ChdCdTrackLayout(2, 4, 2, 2), image.TrackLayouts[1]);
        Assert.Equal(2U, image.TrackLayouts[1].AlignmentFrames);
        Span<byte> bytes = stackalloc byte[2448];
        Assert.Equal(2352, image.ReadSector(2, 0, ChdCdSectorFormat.Raw2352, bytes));
        Assert.True(bytes[..2352].SequenceEqual(logical.AsSpan(4 * 2448, 2352)));
        Assert.Equal(2352, image.ReadSector(2, 1, ChdCdSectorFormat.Raw2352, bytes));
        Assert.True(bytes[..2352].SequenceEqual(logical.AsSpan(5 * 2448, 2352)));
        Assert.Throws<ArgumentOutOfRangeException>(() => image.ReadSector(1, 3, ChdCdSectorFormat.Raw2352, new byte[2352]));
        Assert.Throws<ArgumentOutOfRangeException>(() => image.ReadSector(2, 2, ChdCdSectorFormat.Raw2352, new byte[2352]));
        image.ReadFrame(3, bytes);
        Assert.True(bytes.SequenceEqual(logical.AsSpan(3 * 2448, 2448)));
    }

    /// <summary>A stored pregap declaring a data type other than its track's has no MAME-defined frame layout.</summary>
    [Fact]
    public void StoredPregapWithAnotherDataTypeIsRejected()
    {
        using ChdFile file = Open(Pattern(8),
            "TRACK:1 TYPE:MODE1_RAW SUBTYPE:NONE FRAMES:3 PREGAP:0 PGTYPE:MODE1 PGSUB:NONE POSTGAP:0",
            "TRACK:2 TYPE:AUDIO SUBTYPE:NONE FRAMES:2 PREGAP:1 PGTYPE:VMODE1 PGSUB:NONE POSTGAP:0");
        ChdException error = Assert.Throws<ChdException>(() => ChdCdImage.Open(file, leaveOpen: true));
        Assert.Equal(ChdError.InvalidMetadata, error.Error);
        Assert.Contains("track 2", error.Message, StringComparison.Ordinal);
    }

    /// <summary>chdman writes PGSUB:NONE for every pregap and MAME never reads it, so stored pregap frames carry the track's subcode.</summary>
    [Fact]
    public void StoredPregapSubcodeFollowsTheTrackLayout()
    {
        byte[] logical = Pattern(8);
        using ChdFile file = Open(logical,
            "TRACK:1 TYPE:MODE1_RAW SUBTYPE:NONE FRAMES:3 PREGAP:0 PGTYPE:MODE1 PGSUB:NONE POSTGAP:0",
            "TRACK:2 TYPE:AUDIO SUBTYPE:RW_RAW FRAMES:2 PREGAP:1 PGTYPE:VAUDIO PGSUB:NONE POSTGAP:0");
        using ChdCdImage image = ChdCdImage.Open(file, leaveOpen: true);
        Span<byte> subcode = stackalloc byte[96];
        Assert.Equal(96, image.ReadSector(2, 0, ChdCdSectorFormat.Subcode96, subcode));
        Assert.True(subcode.SequenceEqual(logical.AsSpan(4 * 2448 + 2352, 96)));
    }

    /// <summary>A padded track table that disagrees with the stored geometry names both frame counts.</summary>
    [Fact]
    public void PaddedTrackTableMismatchNamesBothFrameCounts()
    {
        using ChdFile file = Open(Pattern(4), "TRACK:1 TYPE:MODE1 SUBTYPE:NONE FRAMES:5");
        ChdException error = Assert.Throws<ChdException>(() => ChdCdImage.Open(file, leaveOpen: true));
        Assert.Equal(ChdError.InvalidData, error.Error);
        Assert.Contains("covers 8 frames", error.Message, StringComparison.Ordinal);
        Assert.Contains("stores 4", error.Message, StringComparison.Ordinal);
    }

    /// <summary>Legacy binary CHCD metadata is named as unsupported rather than reported as missing metadata.</summary>
    [Fact]
    public void LegacyBinaryCdMetadataIsNamedAsUnsupported()
    {
        using ChdFile file = OpenTagged(Pattern(4), [(ChdMetadataTag.FromFourCC("CHCD"), "binary")]);
        NotSupportedException error = Assert.Throws<NotSupportedException>(() => ChdCdImage.Open(file, leaveOpen: true));
        Assert.Contains("CHCD", error.Message, StringComparison.Ordinal);
    }

    /// <summary>Cooked semantic subcode follows data bytes, rather than a fixed raw-sector offset.</summary>
    [Fact]
    public void CookedSubcodeDoesNotIncludeFramePadding()
    {
        byte[] logical = Pattern(4);
        using ChdFile file = Open(logical, "TRACK:1 TYPE:MODE1 SUBTYPE:RW FRAMES:3");
        using ChdCdImage image = ChdCdImage.Open(file, leaveOpen: true);
        Span<byte> subcode = stackalloc byte[96];
        Assert.Equal(96, image.ReadSector(1, 1, ChdCdSectorFormat.Subcode96, subcode));
        Assert.True(subcode.SequenceEqual(logical.AsSpan(2448 + 2048, 96)));
        Assert.False(subcode.SequenceEqual(logical.AsSpan(2448 + 2352, 96)));
        Assert.Throws<NotSupportedException>(() => image.ReadSector(1, 0, ChdCdSectorFormat.Raw2352, new byte[2352]));
    }

    /// <summary>User-data slices are permitted only for unambiguous metadata layouts.</summary>
    [Theory]
    [InlineData("MODE1", 0, 2048)]
    [InlineData("MODE1_RAW", 16, 2048)]
    [InlineData("MODE2_FORM1", 0, 2048)]
    [InlineData("MODE2_FORM2", 0, 2324)]
    public void UserDataHasExactKnownOffsets(string type, int offset, int length)
    {
        byte[] logical = Pattern(4);
        using ChdFile file = Open(logical, $"TRACK:1 TYPE:{type} SUBTYPE:NONE FRAMES:4");
        using ChdCdImage image = ChdCdImage.Open(file, leaveOpen: true);
        byte[] destination = Enumerable.Repeat((byte)0xcc, 2448).ToArray();
        Assert.Equal(length, image.ReadSector(1, 2, ChdCdSectorFormat.UserData, destination));
        Assert.True(destination.AsSpan(0, length).SequenceEqual(logical.AsSpan(2 * 2448 + offset, length)));
        Assert.All(destination[length..], value => Assert.Equal((byte)0xcc, value));
        Assert.Throws<ArgumentException>(() => image.ReadSector(1, 2, ChdCdSectorFormat.UserData, new byte[length - 1]));
    }

    /// <summary>Ambiguous mode 2 regions require an explicit projection.</summary>
    [Theory]
    [InlineData("MODE2", 0)]
    [InlineData("MODE2_FORM_MIX", 0)]
    [InlineData("MODE2_RAW", 16)]
    public void ModeTwoDataIsExplicitAndUserDataIsAmbiguous(string type, int offset)
    {
        byte[] logical = Pattern(4);
        using ChdFile file = Open(logical, $"TRACK:1 TYPE:{type} SUBTYPE:NONE FRAMES:4");
        using ChdCdImage image = ChdCdImage.Open(file, leaveOpen: true);
        Span<byte> bytes = stackalloc byte[2336];
        Assert.Equal(2336, image.ReadSector(1, 0, ChdCdSectorFormat.Mode2Data, bytes));
        Assert.True(bytes.SequenceEqual(logical.AsSpan(offset, 2336)));
        Assert.Throws<NotSupportedException>(() => image.ReadSector(1, 0, ChdCdSectorFormat.UserData, new byte[2352]));
    }

    /// <summary>XA slices validate repeated subheaders and the explicitly selected form before copying.</summary>
    [Theory]
    [InlineData("MODE2_RAW", 16, false)]
    [InlineData("MODE2_RAW", 16, true)]
    [InlineData("MODE2_FORM_MIX", 0, false)]
    [InlineData("MODE2_FORM_MIX", 0, true)]
    public void XaProjectionValidatesFormAndBothSubheaders(string type, int subheaderOffset, bool formTwo)
    {
        byte[] logical = Pattern(4);
        Span<byte> subheader = logical.AsSpan(subheaderOffset, 8);
        subheader[..4].CopyTo(subheader[4..]);
        subheader[2] = subheader[6] = formTwo ? (byte)0x20 : (byte)0;
        using ChdFile file = Open(logical, $"TRACK:1 TYPE:{type} SUBTYPE:NONE FRAMES:4");
        using ChdCdImage image = ChdCdImage.Open(file, leaveOpen: true);
        var format = formTwo ? ChdCdSectorFormat.XaForm2UserData : ChdCdSectorFormat.XaForm1UserData;
        int length = formTwo ? 2324 : 2048;
        byte[] bytes = new byte[2352];
        Assert.Equal(length, image.ReadSector(1, 0, format, bytes));
        Assert.True(bytes.AsSpan(0, length).SequenceEqual(logical.AsSpan(subheaderOffset + 8, length)));
        Array.Fill(bytes, (byte)0xcc);
        var other = formTwo ? ChdCdSectorFormat.XaForm1UserData : ChdCdSectorFormat.XaForm2UserData;
        Assert.Equal(ChdError.InvalidData, Assert.Throws<ChdException>(() => image.ReadSector(1, 0, other, bytes)).Error);
        Assert.All(bytes, value => Assert.Equal((byte)0xcc, value));
        logical[subheaderOffset + 4] ^= 1;
        using ChdFile badFile = Open(logical, $"TRACK:1 TYPE:{type} SUBTYPE:NONE FRAMES:4");
        using ChdCdImage badImage = ChdCdImage.Open(badFile, leaveOpen: true);
        Assert.Equal(ChdError.InvalidData, Assert.Throws<ChdException>(() => badImage.ReadSector(1, 0, format, bytes)).Error);
        Assert.All(bytes, value => Assert.Equal((byte)0xcc, value));
    }

    /// <summary>Malformed or incomplete track tables do not create a projection.</summary>
    [Theory]
    [InlineData("TRACK:2 TYPE:MODE1 SUBTYPE:NONE FRAMES:4", ChdError.InvalidMetadata)]
    [InlineData("TRACK:1 TYPE:MODE1 SUBTYPE:UNKNOWN FRAMES:4", ChdError.InvalidMetadata)]
    [InlineData("TRACK:1 TYPE:MODE1 SUBTYPE:NONE FRAMES:5", ChdError.InvalidData)]
    public void InvalidTrackTablesFailClosed(string metadata, ChdError error)
    {
        using ChdFile file = Open(Pattern(4), metadata);
        Assert.Equal(error, Assert.Throws<ChdException>(() => ChdCdImage.Open(file, leaveOpen: true)).Error);
        Assert.True(file.Header.LogicalBytes > 0);
    }

    /// <summary>Duplicate track numbers and both GD-ROM tag generations are explicitly rejected.</summary>
    [Fact]
    public void DuplicateAndGdTrackTablesAreRejected()
    {
        const string track = "TRACK:1 TYPE:MODE1 SUBTYPE:NONE FRAMES:4";
        using ChdFile duplicate = Open(Pattern(8), track, track);
        Assert.Equal(ChdError.InvalidMetadata, Assert.Throws<ChdException>(() => ChdCdImage.Open(duplicate, leaveOpen: true)).Error);
        foreach (string tag in new[] { "CHGD", "CHGT" })
        {
            using ChdFile gd = OpenTagged(Pattern(4), [(ChdMetadataTag.FromFourCC(tag),
                "TRACK:1 TYPE:MODE1 SUBTYPE:NONE FRAMES:4 PAD:0 PREGAP:0 PGTYPE:MODE1 PGSUB:NONE POSTGAP:0")]);
            Assert.Throws<NotSupportedException>(() => ChdCdImage.Open(gd, leaveOpen: true));
        }
    }

    /// <summary>Combining unique track numbers across legacy and v2 families would contradict MAME's per-family ordinal lookup.</summary>
    [Fact]
    public void MixedTrackFamiliesFailClosedDespiteContiguousNumbers()
    {
        using ChdFile file = OpenTagged(Pattern(8),
        [
            (ChdMetadataTag.CdTrack, "TRACK:1 TYPE:MODE1 SUBTYPE:NONE FRAMES:4"),
            (ChdMetadataTag.CdTrackV2, "TRACK:2 TYPE:AUDIO SUBTYPE:NONE FRAMES:4 PREGAP:0 PGTYPE:AUDIO PGSUB:NONE POSTGAP:0")
        ]);
        Assert.Equal(ChdError.InvalidMetadata, Assert.Throws<ChdException>(() => ChdCdImage.Open(file, leaveOpen: true)).Error);
    }

    /// <summary>Bounds, enum values and owner disposal are programming errors with predictable guards.</summary>
    [Fact]
    public void ViewGuardsBoundsEnumsAndDisposedOwner()
    {
        using ChdFile file = Open(Pattern(4), "TRACK:1 TYPE:MODE1_RAW SUBTYPE:NONE FRAMES:3");
        using ChdCdImage image = ChdCdImage.Open(file, leaveOpen: true);
        Assert.Throws<ArgumentOutOfRangeException>(() => image.ReadFrame(4, new byte[2448]));
        Assert.Throws<ArgumentOutOfRangeException>(() => image.ReadFrame(ulong.MaxValue, new byte[2448]));
        Assert.Throws<ArgumentException>(() => image.ReadFrame(0, new byte[2447]));
        Assert.Throws<ArgumentOutOfRangeException>(() => image.ReadSector(0, 0, ChdCdSectorFormat.Raw2352, new byte[2352]));
        Assert.Throws<ArgumentOutOfRangeException>(() => image.ReadSector(2, 0, ChdCdSectorFormat.Raw2352, new byte[2352]));
        Assert.Throws<ArgumentOutOfRangeException>(() => image.ReadSector(1, 0, (ChdCdSectorFormat)(-1), new byte[2352]));
        file.Dispose();
        Assert.Throws<ObjectDisposedException>(() => { _ = image.Tracks.Length; });
        Assert.Throws<ObjectDisposedException>(() => image.ReadFrame(0, new byte[2448]));
        Assert.Throws<ObjectDisposedException>(() => image.ReadSector(1, 0, ChdCdSectorFormat.Raw2352, new byte[2352]));
    }

    /// <summary>View ownership and failed-open ownership each honor leaveOpen.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ViewDisposalAndFailedCreationHonorOwnership(bool leaveOpen)
    {
        using ChdFile file = Open(Pattern(4), "TRACK:1 TYPE:MODE1 SUBTYPE:NONE FRAMES:4");
        var image = ChdCdImage.Open(file, leaveOpen);
        image.Dispose();
        image.Dispose();
        Assert.Throws<ObjectDisposedException>(() => { _ = image.Tracks.Length; });
        if (leaveOpen)
            Assert.True(file.Header.LogicalBytes > 0);
        else
            Assert.Throws<ObjectDisposedException>(() => file.Header);
        using ChdFile badFile = Open(Pattern(4), "TRACK:2 TYPE:MODE1 SUBTYPE:NONE FRAMES:4");
        Assert.Throws<ChdException>(() => ChdCdImage.Open(badFile, leaveOpen));
        if (leaveOpen)
            Assert.True(badFile.Header.LogicalBytes > 0);
        else
            Assert.Throws<ObjectDisposedException>(() => badFile.Header);
    }

    /// <summary>Explicit audio swapping is reversible and rejects partial samples before writing.</summary>
    [Fact]
    public void AudioSwapIsExplicitAndReversible()
    {
        byte[] samples = [0, 1, 2, 3];
        ChdCdImage.SwapAudioSamples16(samples);
        Assert.Equal(new byte[] { 1, 0, 3, 2 }, samples);
        ChdCdImage.SwapAudioSamples16(samples);
        Assert.Equal(new byte[] { 0, 1, 2, 3 }, samples);
        byte[] partial = [0, 1, 2];
        Assert.Throws<ArgumentException>(() => ChdCdImage.SwapAudioSamples16(partial));
        Assert.Equal(new byte[] { 0, 1, 2 }, partial);
        ChdCdImage.SwapAudioSamples16([]);
    }

    /// <summary>Warm frame and sector projection allocate no managed bytes.</summary>
    [Fact]
    public void FrameAndSectorProjectionAllocateNothingAfterWarmup()
    {
        byte[] logical = Pattern(4);
        using ChdFile file = Open(logical, "TRACK:1 TYPE:MODE1_RAW SUBTYPE:NONE FRAMES:4");
        using ChdCdImage image = ChdCdImage.Open(file, leaveOpen: true);
        Span<byte> bytes = stackalloc byte[2448];
        bool successful = true;
        for (int index = 0; index < 100; index++)
        {
            image.ReadFrame(0, bytes);
            successful &= image.ReadSector(1, 0, ChdCdSectorFormat.UserData, bytes) == 2048;
        }
        // A recurring allocation fails every window; require an exactly zero-allocation
        // steady-state window using the same bounded protocol as metadata operations.
        bool steady = false;
        for (int window = 0; window < 5 && !steady; window++)
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int index = 0; index < 10_000; index++)
            {
                image.ReadFrame(0, bytes);
                successful &= image.ReadSector(1, 0, ChdCdSectorFormat.UserData, bytes) == 2048;
            }
            steady = GC.GetAllocatedBytesForCurrentThread() == before;
        }
        Assert.True(successful);
        Assert.True(steady, "Frame or sector projection allocated in every steady-state window.");
        Assert.True(bytes[..2048].SequenceEqual(logical.AsSpan(16, 2048)));
    }

    private static byte[] Pattern(int frames)
    {
        byte[] bytes = new byte[frames * 2448];
        for (int index = 0; index < bytes.Length; index++)
            bytes[index] = (byte)(index * 7 + (index >> 8));
        return bytes;
    }

    private static ChdFile Open(byte[] logical, params string[] tracks)
    {
        var entries = new (ChdMetadataTag Tag, string Value)[tracks.Length];
        for (int index = 0; index < tracks.Length; index++)
            entries[index] = (tracks[index].Contains("PREGAP:", StringComparison.Ordinal) ? ChdMetadataTag.CdTrackV2 : ChdMetadataTag.CdTrack, tracks[index]);
        return OpenTagged(logical, entries);
    }

    private static ChdFile OpenTagged(byte[] logical, (ChdMetadataTag Tag, string Value)[] entries)
    {
        _ = NativeTestEnvironment.Root;
        const int hunk = 4 * 2448;
        Assert.Equal(0, logical.Length % hunk);
        int hunks = logical.Length / hunk;
        byte[] container = new byte[(hunks + 1) * hunk];
        "MComprHD"u8.CopyTo(container);
        BinaryPrimitives.WriteUInt32BigEndian(container.AsSpan(8), 124);
        BinaryPrimitives.WriteUInt32BigEndian(container.AsSpan(12), 5);
        BinaryPrimitives.WriteUInt64BigEndian(container.AsSpan(32), (ulong)logical.Length);
        BinaryPrimitives.WriteUInt64BigEndian(container.AsSpan(40), 124);
        BinaryPrimitives.WriteUInt64BigEndian(container.AsSpan(48), 124U + (uint)hunks * 4);
        BinaryPrimitives.WriteUInt32BigEndian(container.AsSpan(56), hunk);
        BinaryPrimitives.WriteUInt32BigEndian(container.AsSpan(60), 2448);
        for (int index = 0; index < hunks; index++)
            BinaryPrimitives.WriteUInt32BigEndian(container.AsSpan(124 + index * 4), (uint)index + 1);
        int metadataOffset = 124 + hunks * 4;
        for (int index = 0; index < entries.Length; index++)
        {
            (ChdMetadataTag tag, string value) = entries[index];
            byte[] payload = Encoding.ASCII.GetBytes(value + '\0');
            int next = metadataOffset + 16 + payload.Length;
            BinaryPrimitives.WriteUInt32BigEndian(container.AsSpan(metadataOffset), tag.Value);
            BinaryPrimitives.WriteUInt32BigEndian(container.AsSpan(metadataOffset + 4), 0x01000000U | (uint)payload.Length);
            BinaryPrimitives.WriteUInt64BigEndian(container.AsSpan(metadataOffset + 8), index + 1 < entries.Length ? (ulong)next : 0);
            payload.CopyTo(container, metadataOffset + 16);
            metadataOffset = next;
        }
        Assert.True(metadataOffset <= hunk);
        logical.CopyTo(container, hunk);
        return ChdFile.Open(new MemoryStream(container, writable: false), leaveOpen: false);
    }
}
