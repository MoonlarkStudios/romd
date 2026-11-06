using Romd.Domain.Hashing;
using Shouldly;
using Xunit;

namespace Romd.Domain.Tests.Common;

public class Sha256Tests
{
    [Fact]
    public void Parse_ValidHash_ReturnsValue()
    {
        string validHash = new('a', 64);

        var result = Sha256.Parse(validHash);

        result.ToString().ShouldBe(validHash);
    }

    [Fact]
    public void Parse_UppercaseHash_NormalizesToLowercase()
    {
        string upperHash = new('A', 64);

        var result = Sha256.Parse(upperHash);

        result.ToString().ShouldBe(new string('a', 64));
    }

    [Fact]
    public void Parse_InvalidLength_ThrowsFormatException()
    {
        string shortHash = "abc123";

        Should.Throw<FormatException>(() => Sha256.Parse(shortHash));
    }

    [Fact]
    public void Parse_InvalidCharacters_ThrowsFormatException()
    {
        string invalidHash = new('g', 64);

        Should.Throw<FormatException>(() => Sha256.Parse(invalidHash));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("ghijklmnopqrstuvwxyzghijklmnopqrstuvwxyzghijklmnopqrstuvwxyzghij")]
    public void TryParse_InvalidInput_ReturnsFalse(string? input)
    {
        bool success = Sha256.TryParse(input, out var result);

        success.ShouldBeFalse();
        result.IsEmpty.ShouldBeTrue();
    }

    [Fact]
    public void Default_IsEmpty_ReturnsTrue()
    {
        var defaultHash = default(Sha256);

        defaultHash.IsEmpty.ShouldBeTrue();
    }

    [Fact]
    public void Default_ToString_ReturnsEmptyString()
    {
        var defaultHash = default(Sha256);

        defaultHash.ToString().ShouldBe(string.Empty);
    }

    [Fact]
    public void TryParse_ValidInput_ReturnsTrueAndValue()
    {
        string validHash = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

        bool success = Sha256.TryParse(validHash, out var result);

        success.ShouldBeTrue();
        result.IsEmpty.ShouldBeFalse();
        result.ToString().ShouldBe(validHash);
    }

    [Fact]
    public void ToString_ReturnsLowercaseHex()
    {
        var hash = Sha256.Parse(new string('a', 64));

        string str = hash.ToString();

        str.ShouldBe(new string('a', 64));
    }

    [Fact]
    public void Equality_SameValue_AreEqual()
    {
        var hash1 = Sha256.Parse(new string('a', 64));
        var hash2 = Sha256.Parse(new string('A', 64));

        hash1.ShouldBe(hash2);
    }

    [Fact]
    public void FromSpan_ValidBytes_Works()
    {
        byte[] bytes = new byte[32];
        bytes[0] = 0xab;

        var hash = Sha256.FromSpan(bytes);

        hash.IsEmpty.ShouldBeFalse();
        hash.AsSpan()[0].ShouldBe((byte)0xab);
    }

    [Fact]
    public void FromSpan_InvalidLength_Throws()
    {
        byte[] bytes = new byte[16];

        Should.Throw<ArgumentException>(() => Sha256.FromSpan(bytes));
    }

    [Fact]
    public void ToArray_ReturnsCorrectBytes()
    {
        var hash = Sha256.Parse("ab" + new string('0', 62));

        byte[] bytes = hash.ToArray();

        bytes.Length.ShouldBe(32);
        bytes[0].ShouldBe((byte)0xab);
    }

    [Fact]
    public void ToShortHex_ReturnsPrefix()
    {
        var hash = Sha256.Parse("abcd1234" + new string('0', 56));

        hash.ToShortHex().ShouldBe("abcd1234");
        hash.ToShortHex(4).ShouldBe("abcd");
    }
}

public class Sha1Tests
{
    [Fact]
    public void Parse_ValidHash_ReturnsValue()
    {
        string validHash = new('a', 40);

        var result = Sha1.Parse(validHash);

        result.ToString().ShouldBe(validHash);
    }

    [Fact]
    public void Parse_InvalidLength_ThrowsFormatException()
    {
        string shortHash = "abc123";

        Should.Throw<FormatException>(() => Sha1.Parse(shortHash));
    }

    [Fact]
    public void TryParse_ValidInput_ReturnsTrueAndValue()
    {
        string validHash = "0123456789abcdef0123456789abcdef01234567";

        bool success = Sha1.TryParse(validHash, out var result);

        success.ShouldBeTrue();
        result.ToString().ShouldBe(validHash);
    }

    [Fact]
    public void Default_IsEmpty_ReturnsTrue()
    {
        var defaultHash = default(Sha1);

        defaultHash.IsEmpty.ShouldBeTrue();
        defaultHash.ToString().ShouldBe(string.Empty);
    }
}

public class Md5Tests
{
    [Fact]
    public void Parse_ValidHash_ReturnsValue()
    {
        string validHash = new('a', 32);

        var result = Md5.Parse(validHash);

        result.ToString().ShouldBe(validHash);
    }

    [Fact]
    public void Parse_InvalidLength_ThrowsFormatException()
    {
        string shortHash = "abc123";

        Should.Throw<FormatException>(() => Md5.Parse(shortHash));
    }

    [Fact]
    public void TryParse_ValidInput_ReturnsTrueAndValue()
    {
        string validHash = "0123456789abcdef0123456789abcdef";

        bool success = Md5.TryParse(validHash, out var result);

        success.ShouldBeTrue();
        result.ToString().ShouldBe(validHash);
    }

    [Fact]
    public void Default_IsEmpty_ReturnsTrue()
    {
        var defaultHash = default(Md5);

        defaultHash.IsEmpty.ShouldBeTrue();
        defaultHash.ToString().ShouldBe(string.Empty);
    }
}

public class Crc32Tests
{
    [Fact]
    public void Parse_ValidHash_ReturnsValue()
    {
        string validHash = "abcd1234";

        var result = Crc32.Parse(validHash);

        result.ToString().ShouldBe(validHash);
    }

    [Fact]
    public void Parse_InvalidLength_ThrowsFormatException()
    {
        string shortHash = "abc";

        Should.Throw<FormatException>(() => Crc32.Parse(shortHash));
    }

    [Fact]
    public void TryParse_ValidInput_ReturnsTrueAndValue()
    {
        string validHash = "deadbeef";

        bool success = Crc32.TryParse(validHash, out var result);

        success.ShouldBeTrue();
        result.ToString().ShouldBe(validHash);
    }

    [Fact]
    public void Parse_UppercaseHex_NormalizesToLowercase()
    {
        string upperHash = "DEADBEEF";

        var result = Crc32.Parse(upperHash);

        result.ToString().ShouldBe("deadbeef");
    }

    [Fact]
    public void Default_IsEmpty_ReturnsTrue()
    {
        var defaultHash = default(Crc32);

        defaultHash.IsEmpty.ShouldBeTrue();
        defaultHash.ToString().ShouldBe(string.Empty);
    }

    [Fact]
    public void AllZeros_IsNotEmpty()
    {
        // "00000000" is a valid CRC32 output (unlike cryptographic hashes)
        var crc = Crc32.FromSpan([0x00, 0x00, 0x00, 0x00]);

        crc.IsEmpty.ShouldBeFalse();
        crc.ToString().ShouldBe("00000000");
    }

    [Fact]
    public void FromUInt32_Roundtrips()
    {
        uint value = 0xDEADBEEF;

        var crc = Crc32.FromUInt32(value);

        crc.ToUInt32().ShouldBe(value);
        crc.ToString().ShouldBe("deadbeef");
    }

    [Fact]
    public void Empty_ToUInt32_Throws()
    {
        var crc = default(Crc32);

        Should.Throw<InvalidOperationException>(() => crc.ToUInt32());
    }
}
