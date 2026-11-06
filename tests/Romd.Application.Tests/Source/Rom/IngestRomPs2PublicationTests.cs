using ErrorOr;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Romd.Admin.Application.Catalog;
using Romd.Admin.Application.Common.Persistence;
using Romd.Admin.Application.Common.Realtime;
using Romd.Admin.Application.Libraries;
using Romd.Admin.Application.Source.Dat;
using Romd.Admin.Application.Source.Rom;
using Romd.Admin.Application.Source.Rom.Commands.IngestRom;
using Romd.Admin.Application.Storage.Files;
using Romd.Application.Common.Security;
using Romd.Admin.Application.Titles;
using Romd.Domain.Source.Rom;
using Shouldly;
using Xunit;

namespace Romd.Application.Tests.Source.Rom;

public sealed class IngestRomPs2PublicationTests
{
    [Fact]
    public async Task HandleAsync_SupportedPs2Original_RechecksExactCurrentAuthorityBeforePublication()
    {
        using var fixture = new Fixture();

        var result = await fixture.HandleAsync();

        result.IsError.ShouldBeFalse(result.IsError ? result.FirstError.Code : null);
        result.Value.IsNew.ShouldBeTrue();
        result.Value.ShouldBeOfType<RomIngestResult.WholeFile>().RomFile.Id.ShouldBe(7);
        result.Value.ShouldBeOfType<RomIngestResult.WholeFile>().RomFile.FileId.ShouldBe(2);
        result.Value.ShouldBeOfType<RomIngestResult.WholeFile>().RomFile.StorageEncoding.ShouldBe(RomRepresentationEncoding.Identity);
        result.Value.ShouldBeOfType<RomIngestResult.WholeFile>().RomFile.ContentSha256.ShouldBe(PublisherChdFacts.Content);
        result.Value.ShouldBeOfType<RomIngestResult.WholeFile>().RomFile.Size.ShouldBe(PublisherChdFacts.LogicalBytes);
        result.Value.ShouldBeOfType<RomIngestResult.WholeFile>().RomFile.Sha1.ShouldBe(PublisherChdFacts.LogicalSha1);
        result.Value.ShouldBeOfType<RomIngestResult.WholeFile>().RomFile.Md5.ShouldBe(PublisherChdFacts.LogicalMd5);
        result.Value.ShouldBeOfType<RomIngestResult.WholeFile>().RomFile.Crc32.ShouldBe(PublisherChdFacts.LogicalCrc);
        result.Value.MatchedTitleIds.ShouldBe(new[] { 11 });
        result.Value.PlatformId.ShouldBe(2);
        fixture.ResolvedStatus.ShouldBe(RomPs2DvdResolutionStatus.Supported);
        await fixture.Authority.Received(1).ResolveAsync(fixture.Prepared.Verification.Identity,
            fixture.Prepared.Ps2BootEvidence!.Boot, fixture.Prepared.Eligibility!.InspectedAt, fixture.Token);
        await fixture.Files.Received(1).StageAsync(fixture.Prepared.Artifact, fixture.Token);
        await fixture.Files.DidNotReceiveWithAnyArgs().StageRawAsync(default!, default);
        await fixture.Files.Received(1).PublishStagedAsync(fixture.Staged, fixture.Token);
        await fixture.Roms.Received(1).RecordWholeMeasurementStagedAsync(7, 2, fixture.Prepared.Container, fixture.Prepared.Measurement, fixture.Token);
        await fixture.Observations.Received(1).StageAsync(7, fixture.Receipt, fixture.Prepared.Inspection!, fixture.Token);
        await fixture.FactPublisher.Received(1).CreateLogicalAsync("disc.iso", fixture.Prepared.Verification.Identity, fixture.Actor,
            Arg.Any<DateTimeOffset>(), fixture.Token);
        await fixture.Roms.Received(1).SetPreferredRepresentationStagedAsync(7, 102, fixture.Token);
        await fixture.Dat.Received(1).LinkDatRomsToRomFileAsync(PublisherChdFacts.LogicalSha1, 7, fixture.Token);
        await fixture.Payload.Received(1).RefreshSourceEntryPayloadAssertionsAsync(
            Arg.Is<IReadOnlyCollection<int>>(ids => ids.SequenceEqual(new[] { 31 })), fixture.Token);
        await fixture.Libraries.Received(1).MarkUpdatesAvailableByPlatformAsync(2, fixture.Token);
        await fixture.Titles.Received(1).TrackByTitleIdsAsync(
            Arg.Is<IReadOnlyCollection<int>>(ids => ids.SequenceEqual(new[] { 11 })), fixture.Token);
        await fixture.Outbox.Received(1).EnqueueAsync(AdminRealtimeEventTypes.StorageStatsChanged, fixture.Token);
        await fixture.Outbox.Received(1).EnqueueAsync(AdminRealtimeEventTypes.CoverageStatsChanged, fixture.Token);
        await fixture.Outbox.Received(1).EnqueueAsync(AdminRealtimeEventTypes.HealthStatsChanged, fixture.Token);
        await fixture.Transaction.Received(1).CommitAsync(fixture.Token);
        await fixture.Transaction.DidNotReceiveWithAnyArgs().RollbackAsync(default);
        fixture.Events.IndexOf("stage").ShouldBeLessThan(fixture.Events.IndexOf("begin"));
        fixture.Events.IndexOf("begin").ShouldBeLessThan(fixture.Events.IndexOf("authority"));
        fixture.Events.IndexOf("file-lock").ShouldBeLessThan(fixture.Events.IndexOf("authority"));
        fixture.Events.IndexOf("authority").ShouldBeLessThan(fixture.Events.IndexOf("publish"));
        fixture.Events.IndexOf("measurement").ShouldBeLessThan(fixture.Events.IndexOf("observation"));
        fixture.Events.IndexOf("observation").ShouldBeLessThan(fixture.Events.IndexOf("prefer"));
        fixture.Events.IndexOf("prefer").ShouldBeLessThan(fixture.Events.IndexOf("commit"));
        await fixture.AssertDisposedAsync();
    }

    [Theory]
    [InlineData("missing", RomPs2DvdResolutionStatus.PendingReference)]
    [InlineData("conflicting", RomPs2DvdResolutionStatus.PendingConflictingReferences)]
    public async Task HandleAsync_ReferenceChangedAfterPreparation_RefusesBeforeCommittedPublication(
        string change, RomPs2DvdResolutionStatus expectedStatus)
    {
        using var fixture = new Fixture { CurrentReferenceChange = change };

        var result = await fixture.HandleAsync();

        result.IsError.ShouldBeTrue();
        result.Errors.ShouldBe(new[] { RomEligibilityErrors.ConflictingObservation() });
        fixture.ResolvedStatus.ShouldBe(expectedStatus);
        await fixture.AssertNoCommittedPublicationAsync(1, fixture.Token);
    }

    [Fact]
    public async Task HandleAsync_CancelledDuringCurrentAuthority_RollsBackWithoutCommittedPublication()
    {
        using var fixture = new Fixture { CancelDuringAuthority = true };

        var exception = await Should.ThrowAsync<OperationCanceledException>(() => fixture.HandleAsync());

        exception.CancellationToken.ShouldBe(fixture.Token);
        fixture.Token.IsCancellationRequested.ShouldBeTrue();
        await fixture.AssertNoCommittedPublicationAsync(1, CancellationToken.None);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly CancellationTokenSource _cancellation = new();
        public CancellationToken Token => _cancellation.Token;
        public readonly IRomRepository Roms = Substitute.For<IRomRepository>();
        public readonly IFileStorageService Files = Substitute.For<IFileStorageService>();
        public readonly IStagedFile Staged = Substitute.For<IStagedFile>();
        public readonly IUnitOfWork Work = Substitute.For<IUnitOfWork>();
        public readonly ITransaction Transaction = Substitute.For<ITransaction>();
        public readonly IRomPs2CurrentReferenceAuthority Authority = Substitute.For<IRomPs2CurrentReferenceAuthority>();
        public readonly IDatRepository Dat = Substitute.For<IDatRepository>();
        public readonly ILibraryRepository Libraries = Substitute.For<ILibraryRepository>();
        public readonly ITrackedTitleRepository Titles = Substitute.For<ITrackedTitleRepository>();
        public readonly ITrackedTitleReader TrackedTitles = Substitute.For<ITrackedTitleReader>();
        public readonly ITitlePayloadAvailabilityProjection Payload = Substitute.For<ITitlePayloadAvailabilityProjection>();
        public readonly IRomPayloadAssertionImpactReader Impact = Substitute.For<IRomPayloadAssertionImpactReader>();
        public readonly IAdminEventOutbox Outbox = Substitute.For<IAdminEventOutbox>();
        public readonly IMediaFactPublisher FactPublisher = Substitute.For<IMediaFactPublisher>();
        public readonly IRomMediaObservationStore Observations = Substitute.For<IRomMediaObservationStore>();
        public readonly IRomMediaCreationStore Creations = Substitute.For<IRomMediaCreationStore>();
        public readonly Guid Actor = Guid.Parse("12345678-1111-2222-3333-123456789012");
        public readonly RomRetainedMeasurement Receipt;
        public readonly List<string> Events = [];
        public readonly PublisherChdFacts Facts;
        public readonly PreparedRom.WholeFile Prepared;
        public string? CurrentReferenceChange;
        public bool CancelDuringAuthority;
        public RomPs2DvdResolutionStatus? ResolvedStatus;
        private readonly IngestRomCommandHandler _handler;
        private bool _romStaged;
        private bool _transactionActive;
        private bool _currentAuthorityApproved;
        private bool _measurementRecorded;

        public Fixture()
        {
            Facts = new(false, RomRepresentationEncoding.Identity, RomEncodingProfiles.Identity,
                pendingReference: true, originalPs2: true);
            Prepared = Facts.Prepared.WithPs2Eligibility(
                PublisherChdFacts.Eligibility(Facts.Output, RomRepresentationEncoding.Chd));
            Receipt = new(12, 102, 2, Prepared.Measurement.Id);
            Roms.GetByIdAsync(7, Token).Returns(_ => _romStaged && _measurementRecorded ? PublisherChdFacts.Rom(2, RomRepresentationEncoding.Identity) : null);
            var tempFiles = Substitute.For<ITempFileFactory>();
            tempFiles.CreateAsync(Arg.Any<Stream>(), Token, Arg.Any<Func<ReadOnlyMemory<byte>, bool>?>(),
                    Arg.Any<Func<ReadOnlyMemory<byte>, CancellationToken, Task>?>(), Arg.Is<Func<ReadOnlyMemory<byte>, int>?>(selector => selector != null)).Returns(Facts.Artifact);
            var preparation = Substitute.For<IRomPreparationService>();
            preparation.PrepareAsync(Facts.Artifact, "disc.iso", Token).Returns(Prepared);
            var catalog = Substitute.For<IRomCatalogMatchReader>();
            catalog.ReadAsync(PublisherChdFacts.LogicalSha1, Token).Returns(new RomCatalogMatch([11], [2], []));
            Roms.GetBySha1Async(PublisherChdFacts.LogicalSha1, Token)
                .Returns(_ => _romStaged ? PublisherChdFacts.Rom(2, RomRepresentationEncoding.Identity) : null);
            Files.StageAsync(Prepared.Artifact, Token).Returns(_ =>
            {
                _transactionActive.ShouldBeFalse();
                Events.Add("stage");
                return Staged;
            });
            Work.BeginTransactionAsync(Token).Returns(_ =>
            {
                _transactionActive.ShouldBeFalse();
                _transactionActive = true;
                Events.Add("begin");
                return Transaction;
            });
            var locks = Substitute.For<IFileMutationLock>();
            locks.When(value => value.AcquireAsync(Prepared.Artifact.FileSha256, Token))
                .Do(_ => RecordTransactionEvent("file-lock", Token));
            Authority.ResolveAsync(Arg.Any<RomLogicalIdentity>(), Arg.Any<RomPs2BootLayoutFacts>(),
                Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>()).Returns(async call =>
            {
                var token = call.Arg<CancellationToken>();
                RecordTransactionEvent("authority", token);
                Events.ShouldContain("file-lock");
                call.Arg<RomLogicalIdentity>().ShouldBe(Prepared.Verification.Identity);
                call.Arg<RomPs2BootLayoutFacts>().ShouldBeSameAs(Prepared.Ps2BootEvidence!.Boot);
                call.Arg<DateTimeOffset>().ShouldBe(Prepared.Eligibility!.InspectedAt);
                if (CancelDuringAuthority)
                {
                    _cancellation.Cancel();
                    token.ThrowIfCancellationRequested();
                }
                var resolved = await PublisherChdFacts.CurrentResolutionAsync(Prepared.Verification,
                    Prepared.Ps2BootEvidence!.Boot, Prepared.Eligibility!.InspectedAt, Prepared.Eligibility,
                    CurrentReferenceChange, token);
                if (!resolved.IsError)
                {
                    ResolvedStatus = resolved.Value.Status;
                    _currentAuthorityApproved = resolved.Value.Status == RomPs2DvdResolutionStatus.Supported &&
                        Prepared.Eligibility!.MatchesObservation(resolved.Value.Eligibility!);
                }
                return resolved;
            });
            var publication = Substitute.For<IStagedFilePublication>();
            publication.ReadAfterFlush().Returns(new FileStoreResult(PublisherChdFacts.File(proof: Prepared.Verification), true, false));
            Files.PublishStagedAsync(Staged, Token).Returns(_ =>
            {
                RecordTransactionEvent("publish", Token);
                _currentAuthorityApproved.ShouldBeTrue();
                return publication;
            });
            FactPublisher.CreateLogicalAsync("disc.iso", Prepared.Verification.Identity, Actor, Arg.Any<DateTimeOffset>(), Token).Returns(_ =>
            {
                RecordTransactionEvent("logical", Token);
                _currentAuthorityApproved.ShouldBeTrue();
                _romStaged = true;
                return 7;
            });
            Roms.RecordWholeMeasurementStagedAsync(7, 2, Prepared.Container, Prepared.Measurement, Token).Returns(_ =>
            { RecordTransactionEvent("measurement", Token); _measurementRecorded = true; return Receipt; });
            Observations.StageAsync(7, Receipt, Prepared.Inspection!, Token).Returns(call =>
            {
                RecordTransactionEvent("observation", Token);
                _measurementRecorded.ShouldBeTrue();
                call.Arg<RomLogicalInspection>().Layout.ShouldBe(Prepared.Ps2BootEvidence!.Boot);
                return Task.FromResult<ErrorOr<Success>>(Result.Success);
            });
            Roms.When(value => value.SetPreferredRepresentationStagedAsync(7, 102, Token)).Do(_ => RecordTransactionEvent("prefer", Token));
            Roms.When(value => value.ReleaseRepresentationsStagedAsync(7, Arg.Any<IReadOnlyList<int>>(), Token))
                .Do(_ => RecordTransactionEvent("release", Token));
            Work.When(value => value.FlushAsync(Token)).Do(_ => RecordTransactionEvent("flush", Token));
            Dat.LinkDatRomsToRomFileAsync(PublisherChdFacts.LogicalSha1, 7, Token)
                .Returns(_ => { RecordTransactionEvent("link", Token); return 1; });
            Impact.ReadSourceEntryIdsBySha1Async(PublisherChdFacts.LogicalSha1, Token)
                .Returns(new[] { 31 });
            Payload.When(value => value.RefreshSourceEntryPayloadAssertionsAsync(Arg.Any<IReadOnlyCollection<int>>(), Token))
                .Do(_ => RecordTransactionEvent("payload", Token));
            Libraries.When(value => value.MarkUpdatesAvailableByPlatformAsync(2, Token))
                .Do(_ => RecordTransactionEvent("library", Token));
            Titles.When(value => value.TrackByTitleIdsAsync(Arg.Any<IReadOnlyCollection<int>>(), Token))
                .Do(_ => RecordTransactionEvent("track", Token));
            Outbox.When(value => value.EnqueueAsync(Arg.Any<string>(), Token))
                .Do(_ => RecordTransactionEvent("outbox", Token));
            Transaction.When(value => value.CommitAsync(Token)).Do(_ => RecordTransactionEvent("commit", Token));
            Transaction.When(value => value.RollbackAsync(Arg.Any<CancellationToken>())).Do(_ => Events.Add("rollback"));
            Transaction.When(value => value.DisposeAsync()).Do(_ =>
            {
                _transactionActive.ShouldBeTrue();
                _transactionActive = false;
                Events.Add("dispose-transaction");
            });
            Staged.When(value => value.DisposeAsync()).Do(_ => Events.Add("dispose-stage"));
            var audit = Substitute.For<IAuditContext>();
            audit.ActorId.Returns(Actor);
            audit.IsSystem.Returns(false);
            var publisher = new RomRepresentationPublisher(Files, Work, Roms, TestRomMediaPolicies.Create(), TestRomEncodings.Create(), locks, null,
                FactPublisher, Observations, Creations, audit, TimeProvider.System, requests: Substitute.For<Romd.Admin.Application.Source.Rom.Reconciliation.IMediaReconciliationRequests>(), ps2Authority: Authority);
            _handler = new(Roms, Dat, catalog, tempFiles, preparation, Substitute.For<Romd.Admin.Application.Source.Rom.Compressed.IRomCdIngestor>(), publisher, Outbox, Libraries, TrackedTitles, Titles,
                Impact, Payload, NullLogger<IngestRomCommandHandler>.Instance, TimeProvider.System);
        }

        public async Task<ErrorOr<RomIngestResult>> HandleAsync()
        {
            using var input = new MemoryStream("synthetic-original"u8.ToArray());
            return await _handler.HandleAsync(new(input, "disc.iso"), Token);
        }

        public async Task AssertNoCommittedPublicationAsync(int authorityCalls, CancellationToken rollbackToken)
        {
            // Staging is permitted before the transaction; no CAS publication or database writes follow refusal.
            Files.ReceivedCalls().Select(call => call.GetMethodInfo().Name).ShouldBe(new[] { nameof(IFileStorageService.StageAsync) });
            Roms.ReceivedCalls().Select(call => call.GetMethodInfo().Name).ShouldBe(new[] { nameof(IRomRepository.GetBySha1Async) });
            Work.ReceivedCalls().Select(call => call.GetMethodInfo().Name).ShouldBe(new[] { nameof(IUnitOfWork.BeginTransactionAsync) });
            Authority.ReceivedCalls().Count().ShouldBe(authorityCalls);
            FactPublisher.ReceivedCalls().ShouldBeEmpty();
            Observations.ReceivedCalls().ShouldBeEmpty();
            Creations.ReceivedCalls().ShouldBeEmpty();
            Dat.ReceivedCalls().ShouldBeEmpty();
            Impact.ReceivedCalls().ShouldBeEmpty();
            Payload.ReceivedCalls().ShouldBeEmpty();
            Libraries.ReceivedCalls().ShouldBeEmpty();
            Titles.ReceivedCalls().ShouldBeEmpty();
            Outbox.ReceivedCalls().ShouldBeEmpty();
            await Transaction.Received(1).RollbackAsync(rollbackToken);
            await Transaction.DidNotReceiveWithAnyArgs().CommitAsync(default);
            await AssertDisposedAsync();
        }

        public async Task AssertDisposedAsync()
        {
            await Transaction.Received(1).DisposeAsync();
            await Staged.Received(1).DisposeAsync();
            _transactionActive.ShouldBeFalse();
            Facts.Artifact.ReadCalls.ShouldBe(0);
            Facts.Artifact.DisposeCalls.ShouldBe(1);
        }

        private void RecordTransactionEvent(string name, CancellationToken token)
        {
            token.ShouldBe(Token);
            _transactionActive.ShouldBeTrue();
            Events.Add(name);
        }

        public void Dispose() => _cancellation.Dispose();
    }
}
