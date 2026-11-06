using Romd.Domain.Catalog;
using Shouldly;
using Xunit;

namespace Romd.Domain.Tests.Catalog;

public sealed class ArtworkSelectionTests
{
    [Fact]
    public void FocalPoint_IsRevisionFencedAndResetByAutomaticOrNewImport()
    {
        var selection = Pinned(10);
        selection.TryPinRetained(Asset(10), selection.Revision, 0, 100).ShouldBeTrue();
        selection.FocalX.ShouldBe(0);
        selection.FocalY.ShouldBe(100);
        selection.TryPinRetained(Asset(10), selection.Revision - 1, 30, 40).ShouldBeFalse();
        selection.TryPinRetained(Asset(10), selection.Revision, -1, 50).ShouldBeFalse();
        selection.TryPinRetained(Asset(10), selection.Revision, 50, 101).ShouldBeFalse();
        selection.FocalX.ShouldBe(0);
        selection.FocalY.ShouldBe(100);
        var request = Guid.NewGuid();
        var revision = selection.RequestPin(request);
        selection.FocalX.ShouldBe(0);
        selection.TryPublishPin(request, revision, Asset(20)).ShouldBeTrue();
        selection.FocalX.ShouldBe(50);
        selection.FocalY.ShouldBe(50);
        selection.TryPinRetained(Asset(20), selection.Revision, 20, 80).ShouldBeTrue();
        selection.ReturnToAutomatic();
        selection.FocalX.ShouldBe(50);
        selection.FocalY.ShouldBe(50);
    }

    [Fact]
    public void TryPinRetained_FencesPendingImportAndRejectsStaleChoice()
    {
        var selection = Pinned(10);
        var request = Guid.NewGuid();
        var revision = selection.RequestPin(request);
        selection.TryPinRetained(Asset(20), revision - 1).ShouldBeFalse();
        selection.TryPinRetained(Asset(20), revision).ShouldBeTrue();
        selection.TryPublishPin(request, revision, Asset(30)).ShouldBeFalse();
        selection.PinnedAssetId.ShouldBe(20);
        selection.PendingRequestId.ShouldBeNull();
    }
    [Fact]
    public void RequestPin_WhenAlreadyPinned_PreservesEffectiveArtworkUntilPublication()
    {
        var selection = Pinned(10);
        var request = Guid.NewGuid();

        var revision = selection.RequestPin(request);

        selection.Mode.ShouldBe(ArtworkSelectionMode.Pinned);
        selection.PinnedAssetId.ShouldBe(10);
        selection.PendingRequestId.ShouldBe(request);
        selection.TryPublishPin(request, revision, Asset(20)).ShouldBeTrue();
        selection.PinnedAssetId.ShouldBe(20);
        selection.PendingRequestId.ShouldBeNull();
    }

    [Fact]
    public void RequestPin_RepeatedPendingRequest_DoesNotAdvanceRevision()
    {
        var selection = ArtworkSelection.CreateAutomatic(1, ArtworkRole.Poster);
        var request = Guid.NewGuid();
        var revision = selection.RequestPin(request);

        selection.RequestPin(request).ShouldBe(revision);
        selection.Mode.ShouldBe(ArtworkSelectionMode.Automatic);
        selection.PinnedAssetId.ShouldBeNull();
    }

    [Fact]
    public void TryPublishPin_OlderRequestCannotOverrideNewerChoice()
    {
        var selection = Pinned(10);
        var oldRequest = Guid.NewGuid();
        var oldRevision = selection.RequestPin(oldRequest);
        var newRequest = Guid.NewGuid();
        var newRevision = selection.RequestPin(newRequest);

        selection.TryPublishPin(oldRequest, oldRevision, Asset(20)).ShouldBeFalse();
        selection.PendingRequestId.ShouldBe(newRequest);
        selection.TryPublishPin(newRequest, newRevision, Asset(30)).ShouldBeTrue();
        selection.TryPublishPin(oldRequest, oldRevision, Asset(20)).ShouldBeFalse();
        selection.PinnedAssetId.ShouldBe(30);
    }

    [Fact]
    public void ReturnToAutomatic_InvalidatesPendingImportAndRemovesPin()
    {
        var selection = Pinned(10);
        var request = Guid.NewGuid();
        var revision = selection.RequestPin(request);

        selection.ReturnToAutomatic().ShouldBeGreaterThan(revision);

        selection.TryPublishPin(request, revision, Asset(20)).ShouldBeFalse();
        selection.Mode.ShouldBe(ArtworkSelectionMode.Automatic);
        selection.PinnedAssetId.ShouldBeNull();
        selection.PendingRequestId.ShouldBeNull();
    }

    [Theory]
    [InlineData(false, 1, ArtworkRole.Poster)]
    [InlineData(true, 2, ArtworkRole.Poster)]
    [InlineData(true, 1, ArtworkRole.Hero)]
    public void TryPublishPin_UnusableOrUnrelatedAsset_PreservesPin(bool eligible, int titleId, ArtworkRole role)
    {
        var selection = Pinned(10);
        var request = Guid.NewGuid();
        var revision = selection.RequestPin(request);

        selection.TryPublishPin(request, revision, Asset(20, eligible, titleId, role)).ShouldBeFalse();

        selection.PinnedAssetId.ShouldBe(10);
        selection.PendingRequestId.ShouldBe(request);
    }

    [Fact]
    public void TryFailPin_CurrentRequest_KeepsPreviousArtwork()
    {
        var selection = Pinned(10);
        var request = Guid.NewGuid();
        var revision = selection.RequestPin(request);

        selection.TryFailPin(request, revision).ShouldBeTrue();

        selection.PinnedAssetId.ShouldBe(10);
        selection.Mode.ShouldBe(ArtworkSelectionMode.Pinned);
        selection.PendingRequestId.ShouldBeNull();
        selection.TryPublishPin(request, revision, Asset(20)).ShouldBeFalse();
    }

    [Fact]
    public void TryFailPin_StaleFailure_DoesNotCancelNewerRequest()
    {
        var selection = Pinned(10);
        var oldRequest = Guid.NewGuid();
        var oldRevision = selection.RequestPin(oldRequest);
        var newRequest = Guid.NewGuid();
        selection.RequestPin(newRequest);

        selection.TryFailPin(oldRequest, oldRevision).ShouldBeFalse();

        selection.PendingRequestId.ShouldBe(newRequest);
    }

    [Fact]
    public void Rehydrate_RestartRetainsPinAndPendingFence()
    {
        var selection = Pinned(10);
        var request = Guid.NewGuid();
        var revision = selection.RequestPin(request);

        var restored = ArtworkSelection.Rehydrate(selection.TitleId, selection.Role, selection.Revision,
            selection.Mode, selection.PinnedAssetId, selection.PendingRequestId);

        restored.PinnedAssetId.ShouldBe(10);
        restored.TryPublishPin(request, revision - 1, Asset(20)).ShouldBeFalse();
        restored.TryPublishPin(request, revision, Asset(20)).ShouldBeTrue();
    }

    private static ArtworkSelection Pinned(int assetId) =>
        ArtworkSelection.Rehydrate(1, ArtworkRole.Poster, 1, ArtworkSelectionMode.Pinned, assetId, null);

    private static ArtworkAsset Asset(int id, bool eligible = true, int titleId = 1,
        ArtworkRole role = ArtworkRole.Poster) =>
        new(id, titleId, role, "steamgriddb", "game", "asset",
            new ArtworkVariant(1, "hash-original-v1", "image/png", 600, 900, "original"),
            [new ArtworkVariant(2, "hash-card-v1", "image/webp", 300, 450, "card")], eligible, DateTimeOffset.UnixEpoch);
}
