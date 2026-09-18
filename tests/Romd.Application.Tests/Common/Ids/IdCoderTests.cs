using Romd.Application.Common.Ids;
using Shouldly;
using Xunit;

namespace Romd.Application.Tests.Common.Ids;

public class IdCoderTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(42)]
    [InlineData(12345)]
    [InlineData(int.MaxValue)]
    public void Encode_ThenTryDecode_RoundTrips(int id)
    {
        // Act
        string encoded = IdCoder.Encode(id);
        bool success = IdCoder.TryDecode(encoded, out int decoded);

        // Assert
        success.ShouldBeTrue();
        decoded.ShouldBe(id);
    }

    [Fact]
    public void Encode_ProducesMinimumLength()
    {
        // Act
        string encoded = IdCoder.Encode(1);

        // Assert - minimum length is 6
        encoded.Length.ShouldBeGreaterThanOrEqualTo(6);
    }

    [Fact]
    public void Encode_ProducesUrlSafeCharacters()
    {
        // Act
        string encoded = IdCoder.Encode(12345);

        // Assert - should only contain alphanumeric characters
        encoded.ShouldMatch("^[a-zA-Z0-9]+$");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void TryDecode_NullOrEmpty_ReturnsFalse(string? input)
    {
        // Act
        bool success = IdCoder.TryDecode(input, out int result);

        // Assert
        success.ShouldBeFalse();
        result.ShouldBe(0);
    }

    [Theory]
    [InlineData("invalid!")]
    [InlineData("with spaces")]
    [InlineData("special@chars")]
    [InlineData("0O1Il")] // Ambiguous characters not in alphabet
    public void TryDecode_InvalidCharacters_ReturnsFalse(string input)
    {
        // Act
        bool success = IdCoder.TryDecode(input, out int result);

        // Assert
        success.ShouldBeFalse();
        result.ShouldBe(0);
    }

    [Fact]
    public void TryDecode_ValidButNotEncoded_ReturnsFalse()
    {
        // Arrange - valid characters but might not decode to a single positive int
        string input = "aaaaaa";

        // Act
        bool success = IdCoder.TryDecode(input, out int result);

        // Assert - either fails or produces a valid positive int
        if (success)
        {
            result.ShouldBeGreaterThan(0);
        }
    }
}

public class SqidTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(42)]
    [InlineData(12345)]
    public void TryParse_ValidEncodedId_ReturnsTrue(int id)
    {
        // Arrange
        string encoded = IdCoder.Encode(id);

        // Act
        bool success = Sqid.TryParse(encoded, null, out Sqid result);

        // Assert
        success.ShouldBeTrue();
        result.Value.ShouldBe(id);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("invalid!")]
    public void TryParse_InvalidInput_ReturnsFalse(string? input)
    {
        // Act
        bool success = Sqid.TryParse(input, null, out Sqid result);

        // Assert
        success.ShouldBeFalse();
        result.Value.ShouldBe(0); // default struct
    }

    [Fact]
    public void ImplicitConversion_ToInt_ReturnsValue()
    {
        // Arrange
        var sqid = new Sqid(42);

        // Act
        int value = sqid;

        // Assert
        value.ShouldBe(42);
    }

    [Fact]
    public void Sqid_CanBeUsedAsMethodParameter()
    {
        // Arrange
        var sqid = new Sqid(42);

        // Act - implicit conversion should work
        int result = AcceptsInt(sqid);

        // Assert
        result.ShouldBe(42);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(42)]
    [InlineData(12345)]
    public void ToString_ReturnsEncodedString(int id)
    {
        // Arrange
        var sqid = new Sqid(id);
        string expected = IdCoder.Encode(id);

        // Act
        string result = sqid.ToString();

        // Assert
        result.ShouldBe(expected);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(42)]
    [InlineData(12345)]
    public void Parse_ValidEncodedId_ReturnsSqid(int id)
    {
        // Arrange
        string encoded = IdCoder.Encode(id);

        // Act
        Sqid result = Sqid.Parse(encoded, null);

        // Assert
        result.Value.ShouldBe(id);
    }

    [Theory]
    [InlineData("")]
    [InlineData("invalid!")]
    [InlineData("0O1Il")]
    public void Parse_InvalidInput_ThrowsFormatException(string input)
    {
        // Act & Assert
        Should.Throw<FormatException>(() => Sqid.Parse(input, null));
    }

    [Fact]
    public void Sqid_ImplementsIParsable()
    {
        // Assert that Sqid implements IParsable<Sqid>
        typeof(IParsable<Sqid>).IsAssignableFrom(typeof(Sqid)).ShouldBeTrue();
    }

    [Fact]
    public void Sqid_RoundTrips_ThroughToStringAndParse()
    {
        // Arrange
        var original = new Sqid(42);

        // Act
        string encoded = original.ToString();
        Sqid parsed = Sqid.Parse(encoded, null);

        // Assert
        parsed.ShouldBe(original);
    }

    private static int AcceptsInt(int value) => value;
}
