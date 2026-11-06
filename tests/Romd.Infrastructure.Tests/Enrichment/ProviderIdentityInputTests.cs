using Romd.Infrastructure.Enrichment;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Enrichment;

public sealed class ProviderIdentityInputTests
{
    [Theory]
    [InlineData("42", "42")]
    [InlineData(" 0042 ", "42")]
    [InlineData("https://www.steamgriddb.com/game/42", "42")]
    [InlineData("https://steamgriddb.com/game/42/", "42")]
    [InlineData("https://steamgriddb.com.attacker.example/game/42", null)]
    [InlineData("https://user@steamgriddb.com/game/42", null)]
    [InlineData("https://steamgriddb.com:444/game/42", null)]
    [InlineData("https://steamgriddb.com/game/42?redirect=evil", null)]
    [InlineData("http://steamgriddb.com/game/42", null)]
    [InlineData("0", null)]
    [InlineData("42; fields *;", null)]
    [InlineData("", null)]
    public void SteamGridDb_AcceptsOnlyCanonicalIdsAndTrustedPageUrls(string input, string? expected) =>
        ProviderIdentityInput.Parse(input, "steamgriddb.com", "/game/").ShouldBe(expected);

    [Fact]
    public void Igdb_AcceptsGameSlugWithoutTreatingItAsAnArbitraryUrl() =>
        ProviderIdentityInput.Parse("https://www.igdb.com/games/super-metroid", "igdb.com", "/games/", true).ShouldBe("super-metroid");
}
