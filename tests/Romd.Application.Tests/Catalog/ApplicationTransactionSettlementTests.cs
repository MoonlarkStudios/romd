using ErrorOr;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Romd.Admin.Application.Artwork;
using Romd.Admin.Application.Catalog;
using Romd.Admin.Application.Catalog.Commands.BackfillBiosCatalog;
using Romd.Admin.Application.Catalog.Commands.DecideOriginalDiscLayout;
using Romd.Admin.Application.Catalog.Commands.SetSourceStatus;
using Romd.Admin.Application.Common.Persistence;
using Romd.Admin.Application.Common.Realtime;
using Romd.Admin.Application.Enrichment;
using Romd.Admin.Application.Ingestion.Jobs;
using Romd.Admin.Application.Ingestion.Jobs.Commands.SetJobArchived;
using Romd.Admin.Application.Libraries;
using Romd.Admin.Application.Libraries.Commands.SetLibraryAttachments;
using Romd.Admin.Application.MetadataProviders;
using Romd.Admin.Application.MetadataProviders.Commands.TestIgdbConnection;
using Romd.Admin.Application.MetadataProviders.Commands.UpdateIgdbSettings;
using Romd.Admin.Application.Source.Dat;
using Romd.Admin.Application.Source.Rom.Reconciliation;
using Romd.Admin.Application.Taxonomy;
using Romd.Admin.Application.Titles;
using Romd.Admin.Application.Titles.Commands.SetEnrichmentOmission;
using Romd.Admin.Application.Titles.ReadModels;
using Romd.Application.Common.Concurrency;
using Romd.Application.Common.Ids;
using Romd.Application.Common.ReferenceCatalog;
using Romd.Application.Common.Security;
using Romd.Application.Common.Systems;
using Romd.Contracts.Common.Models;
using Romd.Contracts.Management.MetadataProviders;
using Romd.Contracts.Management.Models;
using Romd.Domain.Catalog;
using Romd.Domain.Identity;
using Romd.Domain.Jobs;
using Romd.Domain.Source.Dat;
using Romd.Domain.Taxonomy;
using Shouldly;
using Xunit;

namespace Romd.Application.Tests.Catalog;

public sealed class ApplicationTransactionSettlementTests
{
    private static readonly string[] Names = ["artwork-request", "artwork-family", "layout", "archive", "omission", "attachments", "igdb-update", "igdb-test", "backfill", "source", "taxonomy"];
    public static TheoryData<string> Owners => new(Names);
    public static TheoryData<string, bool> Failures
    {
        get
        {
            var cases = new TheoryData<string, bool>();
            foreach (string name in Names)
            { cases.Add(name, false); cases.Add(name, true); }
            return cases;
        }
    }

    [Theory]
    [MemberData(nameof(Owners))]
    public async Task AcknowledgedCommit_SecondaryDisposalFailure_DoesNotReplaceSuccessfulOwnerResult(string owner)
    {
        var work = new Work();
        var operation = Operation(owner, work);

        var result = await operation(CancellationToken.None);

        result.IsError.ShouldBeFalse();
        result.Value.ShouldNotBeNull();
        work.Attempts.ShouldBe(1);
        work.Acknowledged.ShouldBe(1);
        work.Disposals.ShouldBe(1);
        work.Rollbacks.ShouldBe(0);
    }

    [Theory]
    [MemberData(nameof(Failures))]
    public async Task PrimaryFailureAndSecondaryDisposal_PreserveCauseCancellationOrExistingDatabaseTranslation(string owner, bool cancelled)
    {
        var work = new Work();
        using var cancellation = new CancellationTokenSource();
        Exception primary = cancelled ? new OperationCanceledException("Original owner cancellation", cancellation.Token)
            : new IOException("Original owner database failure");
        work.Primary = primary;
        work.BeforeFailure = cancelled ? cancellation.Cancel : null;
        var operation = Operation(owner, work);
        Exception? observed = null;
        Outcome? result = null;

        try
        { result = await operation(cancellation.Token); }
        catch (Exception error) { observed = error; }

        if (!cancelled && owner is "source" or "backfill")
        {
            observed.ShouldBeNull();
            var rejected = result.ShouldNotBeNull();
            rejected.IsError.ShouldBeTrue();
            rejected.Error.Code.ShouldBe("Catalog.DatabaseFailed");
            rejected.Error.Description.ShouldContain(primary.Message);
            rejected.Error.Description.ShouldNotContain(work.Cleanup.Message);
        }
        else
        {
            observed.ShouldBeSameAs(primary);
            if (observed is OperationCanceledException stopped)
                stopped.CancellationToken.ShouldBe(cancellation.Token);
        }
        work.Acknowledged.ShouldBe(0);
        work.Disposals.ShouldBe(1);
        work.Rollbacks.ShouldBe(1);
    }

    [Fact]
    public async Task BusinessRejection_WithoutPrimaryOrCommit_StillSurfacesDisposalFailure()
    {
        var work = new Work();
        var experience = Substitute.For<ILibraryExperienceRepository>();
        experience.SetAttachmentsAsync(1, Arg.Any<IReadOnlyList<(int, bool)>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<ErrorOr<Success>>(Error.Validation("Library.Invalid", "Rejected")));
        var handler = new SetLibraryAttachmentsCommandHandler(experience, work, NullLogger<SetLibraryAttachmentsCommandHandler>.Instance);
        Exception? observed = null;

        try
        { await handler.HandleAsync(new(1, [])); }
        catch (Exception error) { observed = error; }

        observed.ShouldBeSameAs(work.Cleanup);
        work.Attempts.ShouldBe(0);
        work.Disposals.ShouldBe(1);
    }

    [Fact]
    public async Task Taxonomy_OriginalDeadlockSurvivesCleanup_AndOnlyItsExistingOneRetryRuns()
    {
        var work = new Work { Primary = new IOException("Original deadlock"), RetryDeadlock = true, FailOnlyFirst = true };

        var result = await Operation("taxonomy", work)(CancellationToken.None);

        result.IsError.ShouldBeFalse();
        work.Attempts.ShouldBe(2);
        work.Acknowledged.ShouldBe(1);
        work.Disposals.ShouldBe(2);
        work.Rollbacks.ShouldBe(1);
    }

    private static Func<CancellationToken, Task<Outcome>> Operation(string owner, Work work)
    {
        var precondition = RevisionPrecondition.For(Guid.NewGuid().ToString());
        var references = Substitute.For<IReferenceCatalogService>();
        references.GetSystemKeysAsync(Arg.Any<CancellationToken>()).Returns(new SystemKeys(new Dictionary<int, string> { [1] = "system" }));
        switch (owner)
        {
            case "attachments":
                {
                    var store = Substitute.For<ILibraryExperienceRepository>();
                    store.SetAttachmentsAsync(1, Arg.Any<IReadOnlyList<(int, bool)>>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult<ErrorOr<Success>>(Result.Success));
                    var handler = new SetLibraryAttachmentsCommandHandler(store, work, NullLogger<SetLibraryAttachmentsCommandHandler>.Instance);
                    return ct => Capture(handler.HandleAsync(new(1, []), ct));
                }
            case "igdb-update":
            case "igdb-test":
                {
                    var store = Substitute.For<IIgdbProviderSettingsService>();
                    var dto = new IgdbProviderSettingsDto(false, null, false, false, false, null, null, null, null) { Revision = Guid.NewGuid() };
                    store.UpdateStagedAsync(Arg.Any<UpdateIgdbProviderSettingsRequest>(), precondition, Arg.Any<CancellationToken>()).Returns(Task.FromResult<ErrorOr<IgdbProviderSettingsDto>>(dto));
                    store.ProbeAsync(precondition, Arg.Any<CancellationToken>()).Returns(_ =>
                    {
                        work.Begins.ShouldBe(0, "external probing remains outside the transaction");
                        return Task.FromResult<ErrorOr<IgdbProviderTestObservation>>(new IgdbProviderTestObservation(dto.Revision, DateTimeOffset.UnixEpoch, true, "Verified", "fingerprint"));
                    });
                    store.RecordTestStagedAsync(Arg.Any<IgdbProviderTestObservation>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult<ErrorOr<IgdbProviderSettingsDto>>(dto));
                    if (owner == "igdb-update")
                    {
                        var handler = new UpdateIgdbSettingsCommandHandler(store, work, NullLogger<UpdateIgdbSettingsCommandHandler>.Instance);
                        return ct => Capture(handler.HandleAsync(new(new(false, null, null), precondition), ct));
                    }
                    var probe = new TestIgdbConnectionCommandHandler(store, work, NullLogger<TestIgdbConnectionCommandHandler>.Instance);
                    return ct => Capture(probe.HandleAsync(new(precondition), ct));
                }
            case "layout":
                {
                    var store = Substitute.For<IOriginalDiscLayoutService>();
                    store.DecideStagedAsync(1, Arg.Any<OriginalDiscLayoutDecisionRequest>(), precondition, Arg.Any<Guid>(), Arg.Any<CancellationToken>())
                        .Returns(Task.FromResult<ErrorOr<IReadOnlyList<int>>>(Array.Empty<int>()));
                    var dto = new OriginalDiscLayoutDto(IdCoder.Encode(1), Guid.NewGuid(), "accepted", null, "game.cue", "hash", (ByteCount)1,
                        Guid.NewGuid(), "game.cue", "system", null, null, [], new() { Items = [], HasNextPage = false });
                    store.GetAsync(1, Arg.Any<CancellationToken>()).Returns(Task.FromResult<ErrorOr<OriginalDiscLayoutDto>>(dto));
                    var handler = new DecideOriginalDiscLayoutCommandHandler(store, work, Substitute.For<IMediaReconciliationRequests>(), NullLogger<DecideOriginalDiscLayoutCommandHandler>.Instance);
                    return ct => Capture(handler.HandleAsync(new(1, new("reject", null, null, "Reviewed"), precondition, Guid.NewGuid()), ct));
                }
            case "artwork-request":
            case "artwork-family":
                {
                    var store = Substitute.For<IArtworkCurationRepository>();
                    store.StageDeleteAsync(1, 2, precondition, Arg.Any<CancellationToken>()).Returns(Task.FromResult<ErrorOr<bool>>(true));
                    store.GetFamilyAsync(1, Arg.Any<CancellationToken>()).Returns(Task.FromResult<ErrorOr<ArtworkFamily>>(new ArtworkFamily([], "family")));
                    var request = new ArtworkImportRequest(Guid.NewGuid(), 1, ArtworkRole.Poster, "provider", "game", "asset", "https://example.test/art.png", "Artist", null, precondition);
                    var job = ArtworkImportJob.Create(request.RequestId, "Title", 1, 1, request.Role, 1, request.ProviderId, request.ProviderGameId,
                        request.ProviderAssetId, request.TrustedAssetUrl, request.Attribution, DateTimeOffset.UnixEpoch);
                    store.StageRequestAsync(request, Arg.Any<CancellationToken>()).Returns(Task.FromResult<ErrorOr<ArtworkImportJob>>(job));
                    var service = new ArtworkCurationService(store, work, NullLogger<ArtworkCurationService>.Instance);
                    return owner == "artwork-request" ? ct => Capture(service.RequestAsync(request, ct)) : ct => Capture(service.DeleteAsync(1, 2, precondition, ct));
                }
            case "omission":
                {
                    var store = Substitute.For<IEnrichmentWorkbench>();
                    store.SetOmissionAsync(1, "Description", true, precondition, Arg.Any<CancellationToken>()).Returns(Task.FromResult<ErrorOr<Updated>>(Result.Updated));
                    var reader = Substitute.For<ITitleDetailReader>();
                    reader.GetDetailAsync(1, Arg.Any<CancellationToken>()).Returns(new TitleDetailData { Id = 1, Revision = Guid.NewGuid(), PlatformId = 1, Name = "Title", EnrichmentStatus = "None", IsTracked = false });
                    var handler = new SetEnrichmentOmissionCommandHandler(references, store, reader, work, NullLogger<SetEnrichmentOmissionCommandHandler>.Instance);
                    return ct => Capture(handler.HandleAsync(new(1, "Description", true, precondition), ct));
                }
            case "archive":
                {
                    var user = Substitute.For<ICurrentUser>();
                    user.UserId.Returns(Guid.NewGuid());
                    user.HasRole(Arg.Any<RomdRoleType>()).Returns(true);
                    var store = Substitute.For<IJobRepository>();
                    var job = UploadJob.Create("archive.zip", DateTimeOffset.UnixEpoch);
                    job.Fail("Finished", DateTimeOffset.UnixEpoch);
                    store.GetByIdAsync(job.Id, Arg.Any<CancellationToken>()).Returns(job);
                    store.SetArchivedAsync(job.Id, true, Arg.Any<JobVisibilityScope>(), Arg.Any<CancellationToken>()).Returns(_ => { job.Archive(DateTimeOffset.UnixEpoch); return job; });
                    var handler = new SetJobArchivedCommandHandler(store, work, user, references, NullLogger<SetJobArchivedCommandHandler>.Instance);
                    return ct => Capture(handler.HandleAsync(new(job.Id, true), ct));
                }
            case "backfill":
                {
                    var reader = Substitute.For<IBiosRepository>();
                    reader.GetUnmappedBiosGamesAsync(Arg.Any<CancellationToken>()).Returns(new List<UnmappedBiosGame> { new(1, 1, "[BIOS] Example") });
                    var grouper = Substitute.For<IBiosGrouper>();
                    grouper.GroupAsync(1, Arg.Any<IReadOnlyList<(int, string)>>(), Arg.Any<CancellationToken>()).Returns(1);
                    var handler = new BackfillBiosCatalogCommandHandler(reader, grouper, work, NullLogger<BackfillBiosCatalogCommandHandler>.Instance);
                    return ct => Capture(handler.HandleAsync(new(), ct));
                }
            case "source":
                {
                    var dats = Substitute.For<IDatRepository>();
                    dats.GetByIdAsync(1, Arg.Any<CancellationToken>()).Returns(DatFile.CreateNew("DAT", "DAT", DatType.NoIntro, "catalog.dat", 1, DateTimeOffset.UnixEpoch));
                    dats.GetCatalogSourceIdAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(2);
                    var store = Substitute.For<ISourceLifecycle>();
                    store.GetStatusAsync(2, Arg.Any<CancellationToken>()).Returns(CatalogSourceStatus.Active);
                    store.GetEntryPlatformIdsAsync(2, Arg.Any<CancellationToken>()).Returns(new List<int> { 1 });
                    var handler = new SetSourceStatusCommandHandler(dats, store, Substitute.For<ICatalogProjectionService>(), Substitute.For<ILibraryRepository>(),
                        Substitute.For<IAdminEventOutbox>(), work, NullLogger<SetSourceStatusCommandHandler>.Instance);
                    return ct => Capture(handler.HandleAsync(SetSourceStatusCommand.Create(1, "Disabled").Value, ct));
                }
            case "taxonomy":
                {
                    var store = Substitute.For<ITaxonomyRepository<Region>>();
                    store.GetIdByKeyAsync("source", Arg.Any<CancellationToken>()).Returns(1);
                    store.GetIdByKeyAsync("target", Arg.Any<CancellationToken>()).Returns(2);
                    store.LockAsync(Arg.Any<IReadOnlyCollection<(int, TaxonomyRowLock)>>(), Arg.Any<CancellationToken>()).Returns(new HashSet<int> { 1, 2 });
                    var service = new TaxonomyService<Region>(store, work, NullLogger<TaxonomyService<Region>>.Instance);
                    return ct => Capture(service.MergeAsync("source", "target", ct));
                }
            default:
                throw new ArgumentOutOfRangeException(nameof(owner));
        }
    }

    private sealed record Outcome(bool IsError, Error Error, object? Value);
    private static async Task<Outcome> Capture<T>(Task<ErrorOr<T>> operation)
    {
        var result = await operation;
        return result.IsError ? new(true, result.FirstError, null) : new(false, default, result.Value);
    }

    private sealed class Work : IUnitOfWork
    {
        public IOException Cleanup { get; } = new("Secondary disposal failure");
        public Exception? Primary { get; set; }
        public Action? BeforeFailure { get; set; }
        public bool RetryDeadlock { get; set; }
        public bool FailOnlyFirst { get; set; }
        public int Attempts { get; private set; }
        public int Begins { get; private set; }
        public int Acknowledged { get; private set; }
        public int Disposals { get; private set; }
        public int Rollbacks { get; private set; }
        public Task<ITransaction> BeginTransactionAsync(CancellationToken ct) { Begins++; return Task.FromResult<ITransaction>(new Transaction(this)); }
        public Task FlushAsync(CancellationToken ct) => Task.CompletedTask;
        public TransactionConflict ConflictOf(Exception error) => RetryDeadlock && ReferenceEquals(error, Primary) ? TransactionConflict.Deadlock : TransactionConflict.None;
        private sealed class Transaction(Work owner) : ITransaction
        {
            private bool committed;
            public Task CommitAsync(CancellationToken ct)
            {
                owner.Attempts++;
                if (owner.Primary is not null && (!owner.FailOnlyFirst || owner.Attempts == 1))
                { owner.BeforeFailure?.Invoke(); throw owner.Primary; }
                committed = true;
                owner.Acknowledged++;
                return Task.CompletedTask;
            }
            public Task RollbackAsync(CancellationToken ct) { owner.Rollbacks++; throw owner.Cleanup; }
            public ValueTask DisposeAsync() { owner.Disposals++; if (!committed) owner.Rollbacks++; throw owner.Cleanup; }
        }
    }
}
