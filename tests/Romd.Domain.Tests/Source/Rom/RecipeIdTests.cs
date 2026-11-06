using Romd.Domain.Source.Rom;
using Shouldly;
using Xunit;

namespace Romd.Domain.Tests.Source.Rom;

public sealed class RecipeIdTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("recipe\n/v1")]
    [InlineData("recipe\0/v1")]
    public void TryParse_EmptyOrControlCharacters_Rejects(string? value)
    {
        RecipeId.TryParse(value, out _).ShouldBeFalse();
    }
    [Fact]
    public void Parse_BoundaryLengthAndExactText_PreservesPersistedIdentity()
    {
        string value = " " + new string('a', 126) + " ";
        var recipe = RecipeId.Parse(value);
        recipe.Value.ShouldBe(value);
        recipe.ToString().ShouldBe(value);
        recipe.ShouldBe(RecipeId.Parse(value));
        RecipeId.TryParse(value + "a", out _).ShouldBeFalse();
        Should.Throw<ArgumentException>(() => RecipeId.Parse(value + "a"));
    }
    [Theory]
    [InlineData(RomRepresentationEncoding.Identity, false)]
    [InlineData(RomRepresentationEncoding.Rvz, true)]
    [InlineData((RomRepresentationEncoding)999, false)]
    public void IsEncoded_DefinedContainerOnly_RejectsIdentityAndUnknown(RomRepresentationEncoding encoding, bool expected) =>
        encoding.IsEncoded().ShouldBe(expected);
}
