using Xunit;

namespace Moonlark.Libchdr.Tests;

/// <summary>Every frame is checked against source files and fixed geometry, independently of chdman extraction.</summary>
public sealed class ChdCdFixtureV2Tests
{
    /// <summary>File layout, partial hunks, stored and unstored pregaps preserve every source byte.</summary>
    [Theory]
    [InlineData("multi", false, false)]
    [InlineData("single", false, false)]
    [InlineData("partial", true, false)]
    [InlineData("subcode", false, true)]
    public void EveryFrameMatchesSourcesAndTrackBounds(string kind, bool partial, bool subcode)
    {
        using ChdFile file = NativeTestEnvironment.OpenFixture("cd-edge-" + kind + ".chd");
        using ChdCdImage image = ChdCdImage.Open(file, leaveOpen: true);
        int[] frames = subcode ? [8, 6] : partial ? [17, 13] : [17, 13, 9];
        int[] starts = subcode ? [0, 8] : [0, 20, 36];
        int total = subcode ? 16 : partial ? 36 : 48;
        Assert.Equal(frames.Length, image.Tracks.Length);
        Assert.Equal((ulong)total, image.FrameCount);
        Assert.Equal((ulong)total * 2448, file.Header.LogicalBytes);
        Assert.Equal(19584U, file.Header.HunkBytes);
        Assert.Equal(2448U, file.Header.UnitBytes);
        byte[] expected = new byte[total * 2448];
        byte[] single = kind == "single" ? Source("cd-edge-single.bin") : [];
        int singleOffset = 0;
        byte[] actual = new byte[2448];
        for (int track = 0; track < frames.Length; track++)
        {
            byte[] source = kind == "single" ? single.AsSpan(singleOffset, frames[track] * 2352).ToArray()
                : Source(subcode ? $"cd-edge-sub{track + 1}.bin" : $"cd-edge-track{track + 1}.bin");
            singleOffset += frames[track] * 2352;
            Assert.Equal(frames[track] * (subcode ? 2448 : 2352), source.Length);
            Assert.Equal(new ChdCdTrackLayout((uint)track + 1, (ulong)starts[track], (uint)frames[track], (uint)((4 - frames[track] % 4) % 4)), image.TrackLayouts[track]);
            ChdCdTrack metadata = image.Tracks[track];
            Assert.Equal(track == 0 ? (subcode ? ChdCdTrackType.Mode1Raw : ChdCdTrackType.Mode2Raw) : ChdCdTrackType.Audio, metadata.Type);
            Assert.Equal(subcode ? ChdCdSubcodeType.RwRaw : ChdCdSubcodeType.None, metadata.Subtype);
            Assert.Equal(track == 0 ? 0U : 2U, metadata.PregapFrames);
            Assert.Equal(track == 1, metadata.PregapStored);
            Assert.Equal(ChdCdSubcodeType.None, metadata.PregapSubtype);
            for (int frame = 0; frame < frames[track]; frame++)
            {
                Span<byte> projected = expected.AsSpan((starts[track] + frame) * 2448, 2448);
                source.AsSpan(frame * (subcode ? 2448 : 2352), subcode ? 2448 : 2352).CopyTo(projected);
                if (track > 0)
                    for (int index = 0; index < 2352; index += 2)
                        (projected[index], projected[index + 1]) = (projected[index + 1], projected[index]);
                image.ReadFrame((ulong)(starts[track] + frame), actual);
                Assert.True(projected.SequenceEqual(actual));
                Assert.Equal(2352, image.ReadSector((uint)track + 1, (uint)frame, ChdCdSectorFormat.Raw2352, actual));
                Assert.True(projected[..2352].SequenceEqual(actual.AsSpan(0, 2352)));
                if (track > 0)
                {
                    ChdCdImage.SwapAudioSamples16(actual.AsSpan(0, 2352));
                    Assert.True(source.AsSpan(frame * (subcode ? 2448 : 2352), 2352).SequenceEqual(actual.AsSpan(0, 2352)));
                }
                if (subcode)
                {
                    Assert.Equal(96, image.ReadSector((uint)track + 1, (uint)frame, ChdCdSectorFormat.Subcode96, actual));
                    Assert.True(source.AsSpan(frame * 2448 + 2352, 96).SequenceEqual(actual.AsSpan(0, 96)));
                }
            }
            Assert.Throws<ArgumentOutOfRangeException>(() => image.ReadSector((uint)track + 1, (uint)frames[track], ChdCdSectorFormat.Raw2352, actual));
            for (int frame = frames[track]; frame < (frames[track] + 3) / 4 * 4; frame++)
            {
                image.ReadFrame((ulong)(starts[track] + frame), actual);
                Assert.All(actual, value => Assert.Equal((byte)0, value));
            }
        }
        byte[] read = new byte[expected.Length + 29];
        Assert.Equal(expected.Length, file.ReadAt(0, read));
        Assert.True(expected.AsSpan().SequenceEqual(read.AsSpan(0, expected.Length)));
        Assert.Equal(0, file.ReadAt(expected.Length, read));
        using var stream = new ChdStream(file, leaveOpen: true);
        foreach (int offset in new[] { 19584 - 7, 19584 * 4 - 7, expected.Length - 7 }.Where(offset => offset < expected.Length))
        {
            stream.Position = offset;
            int count = Math.Min(29, expected.Length - offset);
            Assert.Equal(count, stream.Read(read, 0, 29));
            Assert.True(expected.AsSpan(offset, count).SequenceEqual(read.AsSpan(0, count)));
        }
        Assert.Equal(-1, stream.ReadByte());
    }

    /// <summary>Every newly generated compressed v2 fixture must have both independently recomputed hashes.</summary>
    [Theory]
    [InlineData("multi")]
    [InlineData("single")]
    [InlineData("partial")]
    [InlineData("subcode")]
    public void NewV2FixturesVerifyBothHashes(string kind)
    {
        using ChdFile file = NativeTestEnvironment.OpenFixture("cd-edge-" + kind + ".chd");
        Assert.True(ChdIntegrity.Verify(file).IsVerified);
    }

    private static byte[] Source(string name) => File.ReadAllBytes(NativeTestEnvironment.Fixture(name));
}
