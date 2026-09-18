using Romd.Domain.Catalog;
using Shouldly;
using Xunit;

namespace Romd.Domain.Tests.Catalog;

public class TrackedTitleTests
{
    [Fact]
    public void Create_InitializesIntentWithoutPin()
    {
        var now = DateTimeOffset.UtcNow;

        var tracked = TrackedTitle.Create(titleId: 10, now);

        tracked.TitleId.ShouldBe(10);
        tracked.PinnedCatalogReleaseId.ShouldBeNull();
        tracked.CreatedAt.ShouldBe(now);
    }

    [Fact]
    public void PinCatalogRelease_SameTitle_SetsPin()
    {
        var tracked = TrackedTitle.Create(10, DateTimeOffset.UtcNow);
        var updatedAt = DateTimeOffset.UtcNow.AddMinutes(1);

        tracked.PinCatalogRelease(catalogReleaseId: 42, releaseTitleId: 10, updatedAt);

        tracked.PinnedCatalogReleaseId.ShouldBe(42);
        tracked.UpdatedAt.ShouldBe(updatedAt);
    }

    [Fact]
    public void PinCatalogRelease_DifferentTitle_Throws()
    {
        var tracked = TrackedTitle.Create(10, DateTimeOffset.UtcNow);

        Should.Throw<ArgumentException>(() =>
            tracked.PinCatalogRelease(catalogReleaseId: 42, releaseTitleId: 11, DateTimeOffset.UtcNow));
    }

    [Fact]
    public void ClearPin_RemovesPin()
    {
        var tracked = TrackedTitle.Create(10, DateTimeOffset.UtcNow);
        tracked.PinCatalogRelease(42, 10, DateTimeOffset.UtcNow);

        tracked.ClearPin(DateTimeOffset.UtcNow);

        tracked.PinnedCatalogReleaseId.ShouldBeNull();
    }
}
