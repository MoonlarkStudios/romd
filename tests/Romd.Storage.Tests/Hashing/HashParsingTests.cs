using Romd.Domain.Hashing;
using Xunit;

namespace Romd.Storage.Tests.Hashing;

public class HashParsingTests
{
    [Theory]
    [MemberData(nameof(TestContents.All), MemberType = typeof(TestContents))]
    public void Sha256_ParseAndFormat_Roundtrips(ContentTestData content)
    {
        var parsed = HexConverter.Parse<Sha256>(content.Sha256);

        Assert.Equal(content.Sha256, parsed.ToString());
        Assert.Equal(content.Sha256.ToUpperInvariant(), parsed.ToUpperHex());
    }

    [Theory]
    [MemberData(nameof(TestContents.All), MemberType = typeof(TestContents))]
    public void Sha1_ParseAndFormat_Roundtrips(ContentTestData content)
    {
        var parsed = HexConverter.Parse<Sha1>(content.Sha1);

        Assert.Equal(content.Sha1, parsed.ToString());
        Assert.Equal(content.Sha1.ToUpperInvariant(), parsed.ToUpperHex());
    }

    [Theory]
    [MemberData(nameof(TestContents.All), MemberType = typeof(TestContents))]
    public void Md5_ParseAndFormat_Roundtrips(ContentTestData content)
    {
        var parsed = HexConverter.Parse<Md5>(content.Md5);

        Assert.Equal(content.Md5, parsed.ToString());
        Assert.Equal(content.Md5.ToUpperInvariant(), parsed.ToUpperHex());
    }

    [Theory]
    [MemberData(nameof(TestContents.All), MemberType = typeof(TestContents))]
    public void Crc32_ParseAndFormat_Roundtrips(ContentTestData content)
    {
        var parsed = HexConverter.Parse<Crc32>(content.Crc32);

        Assert.Equal(content.Crc32, parsed.ToString());
        Assert.Equal(content.Crc32.ToUpperInvariant(), parsed.ToUpperHex());
    }

    [Fact]
    public void Parse_UppercaseHex_Succeeds()
    {
        string upper = TestContents.Abc.Sha1.ToUpperInvariant();
        var parsed = HexConverter.Parse<Sha1>(upper);

        Assert.Equal(TestContents.Abc.Sha1, parsed.ToString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("zzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzz")] // 40 chars but invalid hex
    public void Sha1_Parse_InvalidInput_Throws(string input) =>
        Assert.Throws<FormatException>(() => HexConverter.Parse<Sha1>(input));

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("zzzzzzzz")] // 8 chars but invalid hex
    public void Crc32_Parse_InvalidInput_Throws(string input) =>
        Assert.Throws<FormatException>(() => HexConverter.Parse<Crc32>(input));
}
