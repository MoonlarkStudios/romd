using Romd.Domain.Hashing;
using Xunit;

namespace Romd.Storage.Tests.Hashing;

public class HashEqualityTests
{
    [Fact]
    public void Sha1_EqualityOperators_Work()
    {
        var a = HexConverter.Parse<Sha1>(TestContents.Abc.Sha1);
        var b = HexConverter.Parse<Sha1>(TestContents.Abc.Sha1);
        var c = HexConverter.Parse<Sha1>(TestContents.Digits.Sha1);

        Assert.True(a == b);
        Assert.False(a != b);
        Assert.True(a != c);
        Assert.False(a == c);
    }

    [Fact]
    public void Sha1_GetHashCode_ConsistentForEqualValues()
    {
        var a = HexConverter.Parse<Sha1>(TestContents.Abc.Sha1);
        var b = HexConverter.Parse<Sha1>(TestContents.Abc.Sha1);

        Assert.Equal(a.GetHashCode(), b.GetHashCode());
    }

    [Fact]
    public void Crc32_EqualityOperators_Work()
    {
        var a = HexConverter.Parse<Crc32>(TestContents.Abc.Crc32);
        var b = HexConverter.Parse<Crc32>(TestContents.Abc.Crc32);
        var c = HexConverter.Parse<Crc32>(TestContents.Digits.Crc32);

        Assert.True(a == b);
        Assert.False(a != b);
        Assert.True(a != c);
    }

    [Fact]
    public void Default_IsEmpty()
    {
        Assert.True(default(Sha256).IsEmpty);
        Assert.True(default(Sha1).IsEmpty);
        Assert.True(default(Md5).IsEmpty);
        Assert.True(default(Crc32).IsEmpty);
    }

    [Fact]
    public void Empty_ToString_ReturnsEmptyString()
    {
        Assert.Equal("", default(Sha256).ToString());
        Assert.Equal("", default(Sha1).ToString());
        Assert.Equal("", default(Md5).ToString());
        Assert.Equal("", default(Crc32).ToString());
    }
}
