using Romd.Admin.Application.Source.Dat;
using Shouldly;
using Xunit;

namespace Romd.Application.Tests.Source.Dat;

public sealed class DatEntryIdTests
{
    [Theory]
    [InlineData("Game (USA)")]
    [InlineData("a/b")]
    [InlineData("a%2Fb")]
    [InlineData("Pokémon \U0001F600")]
    public void Encode_RoundTripsThroughAPathSafeId(string name)
    {
        string id = DatEntryId.Encode(name);
        id.ShouldAllBe(character => char.IsAsciiLetterOrDigit(character) || character == '-' || character == '_');
        DatEntryId.TryDecode(id, out string decoded).ShouldBeTrue();
        decoded.ShouldBe(name);
    }

    [Fact]
    public void Encode_DistinguishesASlashFromItsPercentEncoding() =>
        DatEntryId.Encode("a/b").ShouldNotBe(DatEntryId.Encode("a%2Fb"));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("R2FtZQ==")]
    [InlineData("R2FtZQ+")]
    [InlineData("R2FtZR")]
    [InlineData("_w")]
    public void TryDecode_RejectsNonCanonicalOrInvalidIds(string? id) => DatEntryId.TryDecode(id, out _).ShouldBeFalse();

    [Fact]
    public void TryDecode_RejectsIdsLongerThanTheLimit()
    {
        DatEntryId.TryDecode(DatEntryId.Encode(new string('a', 1536)), out _).ShouldBeTrue();
        DatEntryId.TryDecode(DatEntryId.Encode(new string('a', 1537)), out _).ShouldBeFalse();
    }
}
