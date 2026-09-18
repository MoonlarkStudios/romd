using Romd.Domain.Catalog;
using Romd.Domain.Jobs;
using Shouldly;
using Xunit;

namespace Romd.Domain.Tests.Jobs;

public sealed class ArtworkImportJobTests
{
    [Fact]
    public void Create_AcceptedRequest_PreservesIdentityAndRevision()
    {
        var requestId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var now = DateTimeOffset.UnixEpoch.AddDays(7);

        var job = ArtworkImportJob.Create(requestId, "Title", 7, 2, ArtworkRole.Hero, 42,
            "steamgriddb", "game-1", "asset-2", AssetUrl, "Artist", new FixedTimeProvider(now), userId);

        job.Id.ShouldBe(requestId);
        job.CorrelationId.ShouldNotBe(Guid.Empty);
        job.TitleId.ShouldBe(7);
        job.PlatformId.ShouldBe(2);
        job.SourceFilename.ShouldBe("Title");
        job.Role.ShouldBe(ArtworkRole.Hero);
        job.SelectionRevision.ShouldBe(42);
        job.ProviderId.ShouldBe("steamgriddb");
        job.ProviderGameId.ShouldBe("game-1");
        job.ProviderAssetId.ShouldBe("asset-2");
        job.TrustedAssetUrl.ShouldBe(AssetUrl);
        job.Attribution.ShouldBe("Artist");
        job.CreatedAt.ShouldBe(now);
        job.CreatedByUserId.ShouldBe(userId);
        job.PhaseEnum.ShouldBe(ArtworkImportPhase.Pending);
        job.IsTerminal.ShouldBeFalse();
        job.ProgressPercent.ShouldBe(0);
    }

    [Fact]
    public void Create_EmptyRequestIdentity_RejectsRequest()
    {
        Should.Throw<ArgumentException>(() => ArtworkImportJob.Create(Guid.Empty, "Title", 1, null,
            ArtworkRole.Poster, 1, "steamgriddb", "game", "asset", AssetUrl, null, TimeProvider.System));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Create_InvalidRevision_RejectsRequest(long revision)
    {
        Should.Throw<ArgumentOutOfRangeException>(() => ArtworkImportJob.Create(Guid.NewGuid(), "Title", 1,
            null, ArtworkRole.Poster, revision, "steamgriddb", "game", "asset", AssetUrl, null, TimeProvider.System));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(99)]
    public void Create_UnsupportedRole_RejectsRequest(int role)
    {
        Should.Throw<ArgumentOutOfRangeException>(() => ArtworkImportJob.Create(Guid.NewGuid(), "Title", 1,
            null, (ArtworkRole)role, 1, "steamgriddb", "game", "asset", AssetUrl, null, TimeProvider.System));
    }

    [Theory]
    [InlineData("", "game", "asset")]
    [InlineData("steamgriddb", " ", "asset")]
    [InlineData("steamgriddb", "game", "")]
    public void Create_MissingProviderIdentity_RejectsRequest(string provider, string game, string asset)
    {
        Should.Throw<ArgumentException>(() => ArtworkImportJob.Create(Guid.NewGuid(), "Title", 1,
            null, ArtworkRole.Poster, 1, provider, game, asset, AssetUrl, null, TimeProvider.System));
    }

    [Theory]
    [InlineData(51, 1, 1)]
    [InlineData(1, 101, 1)]
    [InlineData(1, 1, 101)]
    public void Create_ProviderIdentityExceedsStorageLimit_RejectsRequest(int provider, int game, int asset)
    {
        Should.Throw<ArgumentOutOfRangeException>(() => ArtworkImportJob.Create(Guid.NewGuid(), "Title", 1,
            null, ArtworkRole.Poster, 1, new string('p', provider), new string('g', game), new string('a', asset),
            AssetUrl, null, TimeProvider.System));
    }

    [Fact]
    public void Start_PendingJob_EntersImporting()
    {
        var job = Create();

        job.Start("delivery-1");

        job.PhaseEnum.ShouldBe(ArtworkImportPhase.Importing);
        job.HangfireJobId.ShouldBe("delivery-1");
        job.StartedAt.ShouldNotBeNull();
        job.IsTerminal.ShouldBeFalse();
        job.ProgressPercent.ShouldBe(50);
    }

    [Fact]
    public void Complete_WithoutPublishedOutcome_RejectsFalseSuccess()
    {
        var job = Create();
        job.Start("delivery-1");

        Should.Throw<InvalidOperationException>(job.Complete);

        job.PhaseEnum.ShouldBe(ArtworkImportPhase.Importing);
        job.CompletedAt.ShouldBeNull();
    }

    [Fact]
    public void Complete_RetainedAsset_RecordsSuccessfulCompletion()
    {
        var job = Create();
        job.Start("delivery-1");
        job.SetCurrentItem("Publishing artwork");
        job.SetImportOutcome(123, false);

        job.Complete();

        job.PhaseEnum.ShouldBe(ArtworkImportPhase.Completed);
        job.IsTerminal.ShouldBeTrue();
        job.ProgressPercent.ShouldBe(100);
        job.RetainedAssetId.ShouldBe(123);
        job.WasSuperseded.ShouldBeFalse();
        job.CompletedAt.ShouldNotBeNull();
        job.CurrentItem.ShouldBeNull();
    }

    [Theory]
    [InlineData(null)]
    [InlineData(123)]
    public void Complete_SupersededRequest_CompletesWithoutClaimingAPin(int? assetId)
    {
        var job = Create();
        job.Start("delivery-1");
        job.SetImportOutcome(assetId, true);

        job.Complete();

        job.PhaseEnum.ShouldBe(ArtworkImportPhase.Completed);
        job.RetainedAssetId.ShouldBe(assetId);
        job.WasSuperseded.ShouldBeTrue();
        job.HasErrors.ShouldBeFalse();
    }

    [Fact]
    public void SetImportOutcome_PendingJob_RejectsPrematurePublication()
    {
        var job = Create();

        Should.Throw<InvalidOperationException>(() => job.SetImportOutcome(1, false));
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    [InlineData(-1)]
    public void SetImportOutcome_NoValidAssetAndNotSuperseded_RejectsOutcome(int? assetId)
    {
        var job = Create();
        job.Start("delivery-1");

        Should.Throw<ArgumentException>(() => job.SetImportOutcome(assetId, false));
    }

    [Fact]
    public void Fail_ImportingJob_RecordsTerminalErrorWithoutOutcome()
    {
        var job = Create();
        job.Start("delivery-1");

        job.Fail("Provider request failed.");

        job.PhaseEnum.ShouldBe(ArtworkImportPhase.Failed);
        job.IsTerminal.ShouldBeTrue();
        job.Errors.Single().Message.ShouldBe("Provider request failed.");
        job.RetainedAssetId.ShouldBeNull();
        job.CompletedAt.ShouldNotBeNull();
        Should.Throw<InvalidOperationException>(() => job.SetImportOutcome(1, false));
    }

    [Fact]
    public void Cancel_ImportingJob_PreventsPublication()
    {
        var job = Create();
        job.Start("delivery-1");

        job.Cancel();

        job.PhaseEnum.ShouldBe(ArtworkImportPhase.Cancelled);
        job.IsTerminal.ShouldBeTrue();
        Should.Throw<InvalidOperationException>(() => job.SetImportOutcome(1, false));
    }

    [Fact]
    public void Rehydrate_ActiveCheckpoint_ResumesWithoutRestartingPhase()
    {
        var original = Create();
        original.Start("delivery-old");
        original.SetImportOutcome(123, false);
        var job = Restore(original);

        job.SetHangfireJobId("delivery-new");
        job.Complete();

        job.PhaseEnum.ShouldBe(ArtworkImportPhase.Completed);
        job.StartedAt.ShouldBe(original.StartedAt);
        job.RetainedAssetId.ShouldBe(123);
        job.HangfireJobId.ShouldBe("delivery-new");
        job.Id.ShouldBe(original.Id);
        job.SelectionRevision.ShouldBe(original.SelectionRevision);
    }

    [Fact]
    public void Rehydrate_ArchivedFailure_PreservesCommonMetadataAndErrors()
    {
        var original = Create();
        original.Start("delivery-1");
        original.SetCurrentItem("Artwork asset");
        original.Fail("Invalid image.");
        original.Archive();

        var restored = Restore(original);

        restored.Id.ShouldBe(original.Id);
        restored.CorrelationId.ShouldBe(original.CorrelationId);
        restored.SourceFilename.ShouldBe(original.SourceFilename);
        restored.PlatformId.ShouldBe(original.PlatformId);
        restored.Role.ShouldBe(original.Role);
        restored.TitleId.ShouldBe(original.TitleId);
        restored.SelectionRevision.ShouldBe(original.SelectionRevision);
        restored.ProviderId.ShouldBe(original.ProviderId);
        restored.ProviderGameId.ShouldBe(original.ProviderGameId);
        restored.ProviderAssetId.ShouldBe(original.ProviderAssetId);
        restored.TrustedAssetUrl.ShouldBe(original.TrustedAssetUrl);
        restored.Attribution.ShouldBe(original.Attribution);
        restored.CurrentItem.ShouldBe(original.CurrentItem);
        restored.Errors.ShouldBe(original.Errors);
        restored.CreatedAt.ShouldBe(original.CreatedAt);
        restored.StartedAt.ShouldBe(original.StartedAt);
        restored.CompletedAt.ShouldBe(original.CompletedAt);
        restored.IsArchived.ShouldBeTrue();
        restored.ArchivedAt.ShouldBe(original.ArchivedAt);
        restored.CreatedByUserId.ShouldBe(original.CreatedByUserId);
        restored.IsTerminal.ShouldBeTrue();
        original.RecordError("later", "Not part of restored snapshot");
        restored.Errors.Count.ShouldBe(1);
    }

    private const string AssetUrl = "https://cdn2.steamgriddb.com/grid/asset.png";

    private static ArtworkImportJob Create() => ArtworkImportJob.Create(Guid.NewGuid(), "Title", 1, 2,
        ArtworkRole.Poster, 9, "steamgriddb", "game", "asset", AssetUrl, null, TimeProvider.System, Guid.NewGuid());

    private static ArtworkImportJob Restore(ArtworkImportJob job) => ArtworkImportJob.Rehydrate(
        job.Id, job.CorrelationId, job.SourceFilename, job.PlatformId, job.PhaseEnum, job.HangfireJobId,
        job.TitleId, job.Role, job.SelectionRevision, job.ProviderId, job.ProviderGameId, job.ProviderAssetId, job.TrustedAssetUrl, job.Attribution,
        job.RetainedAssetId, job.WasSuperseded, job.CurrentItem, job.Errors, job.CreatedAt, job.StartedAt,
        job.CompletedAt, job.IsArchived, job.ArchivedAt, job.CreatedByUserId);

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
