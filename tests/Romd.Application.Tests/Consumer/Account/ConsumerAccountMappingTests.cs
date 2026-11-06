using NSubstitute;
using Romd.Consumer.Application.Account;
using Romd.Consumer.Application.Browse;
using Shouldly;
using Xunit;

namespace Romd.Application.Tests.Consumer.Account;

public sealed class ConsumerAccountMappingTests
{
    private readonly IConsumerTaxonomyKeys _keys = Substitute.For<IConsumerTaxonomyKeys>();

    public ConsumerAccountMappingTests()
    {
        _keys.RegionKeysAsync(Arg.Any<IReadOnlyCollection<int>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<int, string> { [3] = "europe", [5] = "japan" });
        _keys.LanguageKeysAsync(Arg.Any<IReadOnlyCollection<int>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<int, string> { [7] = "en" });
    }

    [Fact]
    public async Task RegisteredOnlyAsync_RepeatedStoredIds_KeepsEachOnceAtItsFirstPosition()
    {
        var stored = new ConsumerReleasePreference([5, 3, 5, 3], [7, 7], ConsumerRevisionPreference.None);

        var preference = await stored.RegisteredOnlyAsync(_keys, default);

        preference.PreferredRegionIds.ShouldBe([5, 3]);
        preference.PreferredLanguageIds.ShouldBe([7]);
    }

    [Fact]
    public async Task ToDtoAsync_IdWithoutAKey_IsDropped()
    {
        var settings = new ConsumerAccountSettings("dark", new ConsumerReleasePreference([9, 3], [7, 8], ConsumerRevisionPreference.NewestFirst));

        var dto = await settings.ToDtoAsync(_keys, default);

        dto.ReleasePreference.PreferredRegionKeys.ShouldBe(["europe"]);
        dto.ReleasePreference.PreferredLanguageKeys.ShouldBe(["en"]);
        dto.ReleasePreference.RevisionStrategy.ShouldBe("newestFirst");
    }
}
