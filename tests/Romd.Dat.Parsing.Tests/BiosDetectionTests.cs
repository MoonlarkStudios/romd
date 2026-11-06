using Shouldly;
using Xunit;

namespace Romd.Dat.Parsing.Tests;

public class BiosDetectionTests
{
    [Fact]
    public void IsBiosGame_IsBiosAttribute_True()
    {
        BiosDetection.IsBiosGame("yes", null, "neogeo").ShouldBeTrue();
    }

    [Fact]
    public void IsBiosGame_CategoryContainsBios_True()
    {
        BiosDetection.IsBiosGame(null, "BIOS", "scph1001").ShouldBeTrue();
    }

    [Fact]
    public void IsBiosGame_NameHasBiosTag_True()
    {
        BiosDetection.IsBiosGame(null, null, "[BIOS] Sony PlayStation (USA)").ShouldBeTrue();
    }

    [Fact]
    public void IsBiosGame_PlainGame_False()
    {
        BiosDetection.IsBiosGame(null, "Games", "Super Mario World (USA)").ShouldBeFalse();
    }

    [Theory]
    [InlineData("Sony - PlayStation 2 - BIOS Images", true)]
    [InlineData("Sega - Mega-CD - BIOS", true)]
    [InlineData("Sony - PlayStation 2", false)]
    [InlineData("Nintendo - Nintendo 64", false)]
    [InlineData(null, false)]
    public void IsBiosDat_DetectsDedicatedBiosSetsByName(string? datName, bool expected)
    {
        BiosDetection.IsBiosDat(datName).ShouldBe(expected);
    }
}
