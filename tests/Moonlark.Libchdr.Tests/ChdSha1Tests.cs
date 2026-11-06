using Xunit;

namespace Moonlark.Libchdr.Tests;

/// <summary>Verifies value lifetime, byte order and allocation-free formatting.</summary>
public sealed class ChdSha1Tests
{
    /// <summary>A published digest vector survives changes to the supplied byte buffer.</summary>
    [Fact]
    public void CopiesBytesAndFormatsIndependentDigest()
    {
        const string expected = "a9993e364706816aba3e25717850c26c9cd0d89d";
        byte[] digest = Convert.FromHexString(expected);
        var value = ChdSha1.FromBytes(digest);
        Span<byte> roundTrip = stackalloc byte[ChdSha1.ByteLength];
        Assert.True(value.TryWriteBytes(roundTrip));
        Assert.True(roundTrip.SequenceEqual(digest));
        Array.Clear(digest);
        Assert.Equal(expected, value.ToString());
        Assert.True(ChdSha1.TryParse(expected.ToUpperInvariant(), out var parsed));
        Assert.Equal(value, parsed);
    }

    /// <summary>Malformed values and short destinations are rejected without partial output.</summary>
    [Fact]
    public void RejectsInvalidAndShortInputs()
    {
        Assert.Throws<ArgumentException>(() => ChdSha1.FromBytes(new byte[19]));
        Assert.Throws<ArgumentException>(() => ChdSha1.FromBytes(new byte[21]));
        foreach (string input in new[] { "", new string('0', 39), new string('0', 41), new string('g', 40), new string('０', 40) })
        {
            Assert.False(ChdSha1.TryParse(input, out var value));
            Assert.True(value.IsEmpty);
        }
        Span<byte> bytes = stackalloc byte[19];
        bytes.Fill(42);
        Assert.False(default(ChdSha1).TryWriteBytes(bytes));
        foreach (byte value in bytes)
            Assert.Equal(42, value);
        Span<char> text = stackalloc char[39];
        text.Fill('!');
        Assert.False(default(ChdSha1).TryFormat(text, out int written));
        Assert.Equal(0, written);
        foreach (char value in text)
            Assert.Equal('!', value);
    }

    /// <summary>Formatting and copying in steady state allocate no managed memory.</summary>
    [Fact]
    public void SpanOperationsAllocateNothing()
    {
        var value = ChdSha1.FromBytes(Convert.FromHexString("a9993e364706816aba3e25717850c26c9cd0d89d"));
        Span<byte> bytes = stackalloc byte[20];
        Span<char> text = stackalloc char[40];
        bool successful = true;
        for (int index = 0; index < 100; index++)
        {
            successful &= value.TryWriteBytes(bytes);
            successful &= value.TryFormat(text, out _);
            successful &= ChdSha1.TryParse(text, out value);
        }
        // One-time runtime transitions (such as tiering under load) may allocate once; a steady-state
        // allocation recurs in every window, so at least one window must allocate nothing.
        bool steady = false;
        for (int window = 0; window < 5 && !steady; window++)
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int index = 0; index < 10_000; index++)
            {
                successful &= value.TryWriteBytes(bytes);
                successful &= value.TryFormat(text, out _);
                successful &= ChdSha1.TryParse(text, out value);
            }
            steady = GC.GetAllocatedBytesForCurrentThread() == before;
        }
        Assert.True(successful);
        Assert.True(steady, "Span operations allocated in every steady-state window.");
        Assert.True(ChdSha1.TryParse(text, out var parsed));
        Assert.Equal(value, parsed);
    }
}
