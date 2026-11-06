using Xunit;

namespace Moonlark.Libchdr.Tests;

/// <summary>Checks CHD's tag byte order, significant spaces and lossless unknown values.</summary>
public sealed class ChdMetadataTagTests
{
    /// <summary>The DVD tag's space and the pinned CHT2 constant remain significant.</summary>
    [Fact]
    public void KnownTagsPreserveByteOrderAndSpaces()
    {
        Assert.Equal(0x43485432U, ChdMetadataTag.FromFourCC("CHT2").Value);
        Assert.Equal(ChdMetadataTag.CdTrackV2, ChdMetadataTag.FromFourCC("CHT2"));
        Assert.Equal(ChdMetadataTag.Dvd, ChdMetadataTag.FromFourCC("DVD "));
        Assert.Equal("DVD ", ChdMetadataTag.Dvd.ToString());
        Assert.Equal("ABCD", new ChdMetadataTag(0x41424344).ToString());
    }

    /// <summary>Binary tags stay lossless; failed text operations leave caller buffers intact.</summary>
    [Fact]
    public void RejectsMalformedTextAndPreservesUnknownBinaryTags()
    {
        foreach (string text in new[] { "DVD", " DVD ", "CHT\0", "ＣHT2" })
        {
            Assert.False(ChdMetadataTag.TryParse(text, out var tag));
            Assert.Equal(ChdMetadataTag.Wildcard, tag);
            Assert.Throws<ArgumentException>(() => ChdMetadataTag.FromFourCC(text));
        }
        var unknown = new ChdMetadataTag(0x01424344);
        Assert.Equal(0x01424344U, unknown.Value);
        Assert.Equal("0x01424344", unknown.ToString());
        Span<char> destination = stackalloc char[4];
        destination.Fill('!');
        Assert.False(unknown.TryFormat(destination, out int written));
        Assert.Equal(0, written);
        Assert.True(destination.SequenceEqual("!!!!"));
        Assert.False(ChdMetadataTag.Dvd.TryFormat(destination[..3], out written));
        Assert.Equal(0, written);
        Assert.True(destination.SequenceEqual("!!!!"));
    }

    /// <summary>Tag parsing and formatting allocate no memory during repeated metadata visits.</summary>
    [Fact]
    public void SpanParsingAndFormattingAllocateNothing()
    {
        Span<char> text = stackalloc char[4];
        var tag = ChdMetadataTag.CdTrackV2;
        bool successful = true;
        for (int index = 0; index < 100; index++)
        {
            successful &= tag.TryFormat(text, out _);
            successful &= ChdMetadataTag.TryParse(text, out tag);
        }
        // A recurring allocation fails every window; one-time runtime transitions do not
        // describe steady-state behavior. Use the same bounded windows as the other value types.
        bool steady = false;
        for (int window = 0; window < 5 && !steady; window++)
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int index = 0; index < 10_000; index++)
            {
                successful &= tag.TryFormat(text, out _);
                successful &= ChdMetadataTag.TryParse(text, out tag);
            }
            steady = GC.GetAllocatedBytesForCurrentThread() == before;
        }
        Assert.True(successful);
        Assert.True(steady, "Tag parsing or formatting allocated in every steady-state window.");
        Assert.Equal(ChdMetadataTag.CdTrackV2, tag);
    }
}
