using Romd.Domain.Catalog;
using Shouldly;
using Xunit;

namespace Romd.Domain.Tests.Catalog;

public class TitleExternalIdTests
{
    [Fact]
    public void CreateNew_DefaultsToUnconfirmed()
    {
        var ext = TitleExternalId.CreateNew(1, "igdb", "12345", 0.85f);

        ext.IsConfirmed.ShouldBeFalse();
        ext.MatchConfidence.ShouldBe(0.85f);
    }

    [Fact]
    public void TryUpdateAutoMatch_WhenUnconfirmed_UpdatesAndReturnsTrue()
    {
        var ext = TitleExternalId.CreateNew(1, "igdb", "12345", 0.85f);

        var result = ext.TryUpdateAutoMatch("99999", 0.95f);

        result.ShouldBeTrue();
        ext.ExternalId.ShouldBe("99999");
        ext.MatchConfidence.ShouldBe(0.95f);
        ext.IsConfirmed.ShouldBeFalse();
    }

    [Fact]
    public void TryUpdateAutoMatch_WhenConfirmed_NoOpAndReturnsFalse()
    {
        var ext = TitleExternalId.CreateNew(1, "igdb", "12345", 0.85f);
        ext.Confirm();

        var result = ext.TryUpdateAutoMatch("99999", 0.95f);

        result.ShouldBeFalse();
        ext.ExternalId.ShouldBe("12345");
        ext.MatchConfidence.ShouldBe(0.85f);
        ext.IsConfirmed.ShouldBeTrue();
    }

    [Fact]
    public void SetManually_AlwaysSucceeds_SetsConfirmedAndFullConfidence()
    {
        var ext = TitleExternalId.CreateNew(1, "igdb", "12345", 0.5f);

        ext.SetManually("99999");

        ext.ExternalId.ShouldBe("99999");
        ext.MatchConfidence.ShouldBe(1.0f);
        ext.IsConfirmed.ShouldBeTrue();
    }

    [Fact]
    public void SetManually_WhenAlreadyConfirmed_StillUpdates()
    {
        var ext = TitleExternalId.CreateNew(1, "igdb", "12345", 0.85f);
        ext.Confirm();

        ext.SetManually("99999");

        ext.ExternalId.ShouldBe("99999");
        ext.MatchConfidence.ShouldBe(1.0f);
        ext.IsConfirmed.ShouldBeTrue();
    }

    [Fact]
    public void Confirm_SetsIsConfirmedTrue()
    {
        var ext = TitleExternalId.CreateNew(1, "igdb", "12345", 0.85f);

        ext.Confirm();

        ext.IsConfirmed.ShouldBeTrue();
        ext.ExternalId.ShouldBe("12345");
        ext.MatchConfidence.ShouldBe(0.85f);
    }

    [Fact]
    public void Confirm_IsIdempotent()
    {
        var ext = TitleExternalId.CreateNew(1, "igdb", "12345", 0.85f);
        ext.Confirm();

        ext.Confirm();

        ext.IsConfirmed.ShouldBeTrue();
    }

    [Fact]
    public void TryUpdateAutoMatch_LowerConfidence_ReturnsFalse()
    {
        var ext = TitleExternalId.CreateNew(1, "igdb", "12345", 0.85f);

        var result = ext.TryUpdateAutoMatch("99999", 0.5f);

        result.ShouldBeFalse();
        ext.ExternalId.ShouldBe("12345");
        ext.MatchConfidence.ShouldBe(0.85f);
    }

    [Fact]
    public void TryUpdateAutoMatch_HigherConfidence_Updates()
    {
        var ext = TitleExternalId.CreateNew(1, "igdb", "12345", 0.5f);

        var result = ext.TryUpdateAutoMatch("99999", 0.85f);

        result.ShouldBeTrue();
        ext.ExternalId.ShouldBe("99999");
        ext.MatchConfidence.ShouldBe(0.85f);
    }

    [Fact]
    public void TryUpdateAutoMatch_EqualConfidence_Updates()
    {
        var ext = TitleExternalId.CreateNew(1, "igdb", "12345", 0.85f);

        var result = ext.TryUpdateAutoMatch("99999", 0.85f);

        result.ShouldBeTrue();
        ext.ExternalId.ShouldBe("99999");
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
