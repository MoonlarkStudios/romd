using Romd.Domain.Hashing;
using Xunit;

namespace Romd.Storage.Tests.Hashing;

/// <summary>
///     CRC32 has special handling because 0x00000000 is a valid checksum.
///     These tests verify the explicit _hasValue flag works correctly.
/// </summary>
public class Crc32SpecificTests
{
    [Fact]
    public void Crc32_AllZeros_IsNotEmpty()
    {
        // "00000000" is a valid CRC32 output (unlike cryptographic hashes)
        var crc = Crc32.FromSpan([0x00, 0x00, 0x00, 0x00]);
        Assert.False(crc.IsEmpty);
        Assert.Equal("00000000", crc.ToString());
    }

    [Fact]
    public void Crc32_Default_IsEmpty()
    {
        var crc = default(Crc32);

        Assert.True(crc.IsEmpty);
        Assert.Equal("", crc.ToString());
    }

    [Fact]
    public void Crc32_FromUInt32_Roundtrips()
    {
        uint value = 0xCBF43926; // CRC32 of the standard "123456789" test vector

        var crc = Crc32.FromUInt32(value);

        Assert.Equal(value, crc.ToUInt32());
        Assert.Equal("cbf43926", crc.ToString());
    }

    [Fact]
    public void Crc32_FromUInt32_Zero_NotEmpty()
    {
        var crc = Crc32.FromUInt32(0);

        Assert.False(crc.IsEmpty);
        Assert.Equal(0u, crc.ToUInt32());
    }

    [Fact]
    public void Crc32_Empty_ToUInt32_Throws()
    {
        var crc = default(Crc32);

        Assert.Throws<InvalidOperationException>(() => crc.ToUInt32());
    }

    [Fact]
    public void Crc32_Comparison_Works()
    {
        var low = Crc32.FromUInt32(0x00000001);
        var high = Crc32.FromUInt32(0xFFFFFFFF);

        Assert.True(low < high);
        Assert.True(high > low);
        Assert.True(low <= high);
        Assert.True(high >= low);
        Assert.Equal(0, low.CompareTo(low));
    }
}
