using System.Text;
using Romd.Consumer.Application.Delivery;
using Shouldly;
using Xunit;

namespace Romd.Application.Tests.Consumer.Delivery;

public sealed class CueDependenciesTests
{
    [Theory]
    [InlineData("FILE \"track one.bin\" BINARY\n TRACK 01 MODE2/2352", true)]
    [InlineData("file track.bin BINARY", true)]
    [InlineData("FILE \"../track.bin\" BINARY", false)]
    [InlineData("FILE \"/track.bin\" BINARY", false)]
    [InlineData("FILE \"missing.bin\" BINARY", false)]
    [InlineData("FILE \"TRACK.BIN\" BINARY", false)]
    [InlineData("FILE \"C:\\track.bin\" BINARY", false)]
    [InlineData("REM no file", false)]
    [InlineData("FILE \"game.cue\" BINARY", false)]
    public void Validate_DescriptorReferences_RequiresExactSafeCompanions(string text, bool expected) =>
        CueDependencies.Validate(Encoding.UTF8.GetBytes(text), "disc/game.cue", ["disc/game.cue", "disc/track.bin", "disc/track one.bin"]).ShouldBe(expected);

    [Fact]
    public void Validate_InvalidUtf8_Rejects() => CueDependencies.Validate([0xff], "game.cue", ["game.cue"]).ShouldBeFalse();
}
