using NSubstitute;
using Romd.Admin.Application.Source.Platform;
using Romd.Domain.Source.Platform;
using Shouldly;
using Xunit;

namespace Romd.Application.Tests.Source.Platform;

public sealed class PlatformHeaderResolverTests
{
    private readonly IPlatformAliasRepository _aliasRepository = Substitute.For<IPlatformAliasRepository>();
    private readonly IPlatformRepository _platformRepository = Substitute.For<IPlatformRepository>();

    private PlatformHeaderResolver CreateResolver(
        IReadOnlyList<Domain.Source.Platform.Platform>? platforms = null,
        IReadOnlyList<(int PlatformId, string NormalizedValue)>? nameAliases = null)
    {
        _platformRepository.GetAllAsync(Arg.Any<CancellationToken>())
            .Returns(platforms ?? []);
        _aliasRepository.GetAllNameAliasesAsync(Arg.Any<CancellationToken>())
            .Returns(nameAliases ?? []);

        return new PlatformHeaderResolver(_platformRepository, _aliasRepository);
    }

    private static Domain.Source.Platform.Platform RehydratePlatform(int id, string name, string shortName) =>
        Domain.Source.Platform.Platform.Rehydrate(id, name, shortName, null, DateTimeOffset.UtcNow);

    [Fact]
    public async Task ResolvePlatformIdAsync_MatchesPlatformName()
    {
        var resolver = CreateResolver([RehydratePlatform(1, "Super Nintendo Entertainment System", "snes")]);

        var result = await resolver.ResolvePlatformIdAsync("Nintendo - Super Nintendo Entertainment System");

        result.ShouldBe(1);
    }

    [Fact]
    public async Task ResolvePlatformIdAsync_MatchesNameAlias()
    {
        var resolver = CreateResolver(
            [RehydratePlatform(1, "Super Nintendo Entertainment System", "snes")],
            [(1, "super famicom")]);

        var result = await resolver.ResolvePlatformIdAsync("Nintendo - Super Famicom");

        result.ShouldBe(1);
    }

    [Fact]
    public async Task ResolvePlatformIdAsync_AmbiguousKey_ReturnsNull()
    {
        // Two platforms claim the same alias — must not route
        var resolver = CreateResolver(
            [
                RehydratePlatform(1, "Super Nintendo Entertainment System", "snes"),
                RehydratePlatform(2, "Super Famicom Deluxe", "sfd")
            ],
            [(1, "super console"), (2, "super console")]);

        var result = await resolver.ResolvePlatformIdAsync("Super Console");

        result.ShouldBeNull();
    }

    [Fact]
    public async Task ResolvePlatformIdAsync_SameKeyFromSamePlatform_StillMatches()
    {
        // A platform whose alias duplicates its own name is not ambiguous
        var resolver = CreateResolver(
            [RehydratePlatform(1, "PlayStation", "psx")],
            [(1, "playstation")]);

        var result = await resolver.ResolvePlatformIdAsync("Sony - PlayStation");

        result.ShouldBe(1);
    }

    [Fact]
    public async Task ResolvePlatformIdAsync_NoMatch_ReturnsNull()
    {
        var resolver = CreateResolver([RehydratePlatform(1, "Super Nintendo Entertainment System", "snes")]);

        var result = await resolver.ResolvePlatformIdAsync("Bandai - WonderSwan");

        result.ShouldBeNull();
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    public async Task ResolvePlatformIdAsync_BlankHeader_ReturnsNullWithoutQuerying(string header)
    {
        var resolver = CreateResolver();

        var result = await resolver.ResolvePlatformIdAsync(header);

        result.ShouldBeNull();
        await _platformRepository.DidNotReceive().GetAllAsync(Arg.Any<CancellationToken>());
    }
}
