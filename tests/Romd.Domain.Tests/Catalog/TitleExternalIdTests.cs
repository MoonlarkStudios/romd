using Romd.Domain.Catalog;
using Shouldly;
using Xunit;

namespace Romd.Domain.Tests.Catalog;

public class TitleExternalIdTests
{
    [Fact]
    public void CreateNew_DefaultsToUnconfirmed()
    {
        var ext = TitleExternalId.CreateNew(1, "igdb", "12345", 0.85f, createdAt: DateTimeOffset.UnixEpoch);

        ext.IsConfirmed.ShouldBeFalse();
        ext.MatchConfidence.ShouldBe(0.85f);
    }

    [Fact]
    public void Rehydrate_PreservesIsConfirmed()
    {
        var ext = TitleExternalId.Rehydrate(
            id: 42,
            titleId: 1,
            provider: "igdb",
            externalId: "12345",
            matchConfidence: 0.9f,
            isConfirmed: true,
            createdAt: DateTimeOffset.UtcNow);

        ext.IsConfirmed.ShouldBeTrue();
    }
}
