using Romd.Domain.Core;
using Shouldly;
using Xunit;

namespace Romd.Domain.Tests.Common;

public class StorageKeyTests
{
    [Theory]
    [InlineData("simple.dat")]
    [InlineData("dats/imported/abc123.dat")]
    [InlineData("deep/nested/path/file.xml")]
    public void Parse_ValidKey_ReturnsValue(string key)
    {
        // Act
        var result = StorageKey.Parse(key);

        // Assert
        result.Value.ShouldBe(key);
    }

    [Fact]
    public void Parse_BackslashPath_NormalizesToForwardSlash()
    {
        // Arrange
        string path = @"dats\imported\file.dat";

        // Act
        var result = StorageKey.Parse(path);

        // Assert
        result.Value.ShouldBe("dats/imported/file.dat");
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("foo/../../../etc/passwd")]
    [InlineData("..")]
    [InlineData("foo/..")]
    public void Parse_PathTraversal_ThrowsFormatException(string key)
    {
        // Act & Assert
        Should.Throw<FormatException>(() => StorageKey.Parse(key));
    }

    [Theory]
    [InlineData("/etc/passwd")]
    [InlineData("/absolute/path")]
    public void Parse_AbsolutePath_ThrowsFormatException(string key)
    {
        // Act & Assert
        var ex = Should.Throw<FormatException>(() => StorageKey.Parse(key));
        ex.Message.ShouldContain("absolute");
    }

    [Theory]
    [InlineData("C:foo")]
    [InlineData("D:bar/baz")]
    public void Parse_DriveLetters_ThrowsFormatException(string key)
    {
        // Act & Assert
        var ex = Should.Throw<FormatException>(() => StorageKey.Parse(key));
        ex.Message.ShouldContain("drive");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void TryParse_InvalidInput_ReturnsFalse(string? input)
    {
        // Act
        bool success = StorageKey.TryParse(input, out var result);

        // Assert
        success.ShouldBeFalse();
        result.IsDefault.ShouldBeTrue();
    }

    [Fact]
    public void TryParse_PathTraversal_ReturnsFalse()
    {
        // Act
        bool success = StorageKey.TryParse("../escape", out var result, out var error);

        // Assert
        success.ShouldBeFalse();
        result.IsDefault.ShouldBeTrue();
        error.ShouldNotBeNull();
        error.ShouldContain("..");
    }

    [Fact]
    public void Default_IsDefault_ReturnsTrue()
    {
        // Arrange
        var defaultKey = default(StorageKey);

        // Assert
        defaultKey.IsDefault.ShouldBeTrue();
    }

    [Fact]
    public void Default_AccessingValue_ThrowsInvalidOperationException()
    {
        // Arrange
        var defaultKey = default(StorageKey);

        // Act & Assert
        Should.Throw<InvalidOperationException>(() => _ = defaultKey.Value);
    }

    [Fact]
    public void Default_ToString_ReturnsDefaultIndicator()
    {
        // Arrange
        var defaultKey = default(StorageKey);

        // Act & Assert
        defaultKey.ToString().ShouldBe("<default>");
    }

    [Fact]
    public void Default_ImplicitConversion_ThrowsInvalidOperationException()
    {
        // Arrange
        var defaultKey = default(StorageKey);

        // Act & Assert
        Should.Throw<InvalidOperationException>(() => { string _ = defaultKey; });
    }

    [Fact]
    public void TryParse_ValidInput_ReturnsTrueAndValue()
    {
        // Arrange
        string validKey = "dats/imported/abc123.dat";

        // Act
        bool success = StorageKey.TryParse(validKey, out var result);

        // Assert
        success.ShouldBeTrue();
        result.IsDefault.ShouldBeFalse();
        result.Value.ShouldBe(validKey);
    }

    [Fact]
    public void ImplicitConversion_ToStringWorks()
    {
        // Arrange
        var key = StorageKey.Parse("test/file.dat");

        // Act
        string str = key;

        // Assert
        str.ShouldBe("test/file.dat");
    }

    [Fact]
    public void ToString_ReturnsValue()
    {
        // Arrange
        var key = StorageKey.Parse("test/file.dat");

        // Act
        string str = key.ToString();

        // Assert
        str.ShouldBe("test/file.dat");
    }

    [Fact]
    public void Equality_SameValue_AreEqual()
    {
        // Arrange
        var key1 = StorageKey.Parse("test/file.dat");
        var key2 = StorageKey.Parse("test/file.dat");

        // Act & Assert
        key1.ShouldBe(key2);
    }

    [Fact]
    public void Equality_NormalizedPaths_AreEqual()
    {
        // Arrange - backslash gets normalized to forward slash
        var key1 = StorageKey.Parse("test/file.dat");
        var key2 = StorageKey.Parse(@"test\file.dat");

        // Act & Assert
        key1.ShouldBe(key2);
    }
}
