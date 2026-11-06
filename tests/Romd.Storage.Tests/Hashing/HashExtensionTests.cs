using Romd.Domain.Hashing;
using Xunit;

namespace Romd.Storage.Tests.Hashing;

public class HashExtensionTests
{
    [Fact]
    public void ToArray_ReturnsCorrectBytes()
    {
        var sha1 = HexConverter.Parse<Sha1>(TestContents.Abc.Sha1);

        byte[] bytes = sha1.ToArray();

        Assert.Equal(20, bytes.Length);
        Assert.Equal(sha1.AsSpan().ToArray(), bytes);
    }

    [Fact]
    public void ToArray_Empty_ReturnsEmptyArray()
    {
        var sha1 = default(Sha1);

        byte[] bytes = sha1.ToArray();

        Assert.Empty(bytes);
    }

    [Fact]
    public void ToShortHex_ReturnsPrefix()
    {
        var sha1 = HexConverter.Parse<Sha1>(TestContents.Abc.Sha1);

        Assert.Equal("a9993e36", sha1.ToShortHex());
        Assert.Equal("a999", sha1.ToShortHex(4));
        Assert.Equal("a9", sha1.ToShortHex(2));
    }

    [Fact]
    public void ToShortHex_Empty_ReturnsEmptyString()
    {
        var sha1 = default(Sha1);

        Assert.Equal("", sha1.ToShortHex());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void TryParse_NullOrEmpty_ReturnsFalse(string? input)
    {
        bool result = HexConverter.TryParse<Sha1>(input, out var sha1);

        Assert.False(result);
        Assert.True(sha1.IsEmpty);
    }

    [Fact]
    public void Parse_Extension_Works()
    {
        var sha1 = HexConverter.Parse<Sha1>(TestContents.Abc.Sha1);

        Assert.Equal(TestContents.Abc.Sha1, sha1.ToString());
    }
}
