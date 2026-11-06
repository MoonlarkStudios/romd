using ErrorOr;
using NSubstitute;
using Romd.Admin.Application.Common.Persistence;
using Romd.Admin.Application.Hashing;
using Romd.Admin.Application.Ingestion.Jobs;
using Romd.Admin.Application.Source.Rom;
using Romd.Admin.Application.Source.Rom.Reconciliation;
using Romd.Admin.Application.Storage.Files;
using Romd.Domain.Hashing;
using Romd.Domain.Jobs;
using Romd.Domain.Source.Rom;
using Romd.Domain.Storage;
using Shouldly;
using Xunit;

namespace Romd.Application.Tests.Content;

public sealed class MediaReconciliationPublicationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Publish_BatchFileIdentities_FlushesOnceOnlyWhenPendingBeforeChecksumsAndFacts(bool pending)
    {
        var f = new Fixture { PendingFiles = pending };
        f.Files.When(files => files.RecordArtifactChecksumsAsync(Arg.Any<int>(), Arg.Any<TempFileArtifactMeasurement>(), Arg.Any<CancellationToken>()))
            .Do(_ => f.Events.Add("checksums"));

        (await f.Publication.PublishAsync(f.Attempt, f.Encoded, true)).IsError.ShouldBeFalse();

        f.Events.Count(value => value == "flush").ShouldBe(pending ? 1 : 0);
        if (pending)
            f.Events.IndexOf("flush").ShouldBeLessThan(f.Events.IndexOf("checksums"));
        f.Events.IndexOf("checksums").ShouldBeLessThan(f.Events.IndexOf("facts"));
        await f.Files.Received(1).PublishStagedAsync(Arg.Is<IReadOnlyList<IStagedFile>>(files => files.Count == 2), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Publish_FileIdentityFlushFails_SkipsChecksumAndFactWritesAndReleasesStages()
    {
        var f = new Fixture { PendingFiles = true };
        f.Work.FlushAsync(Arg.Any<CancellationToken>()).Returns(Task.FromException(new IOException("File identity flush failed.")));

        await Should.ThrowAsync<IOException>(() => f.Publication.PublishAsync(f.Attempt, f.Encoded, true));

        await f.Files.DidNotReceive().RecordArtifactChecksumsAsync(Arg.Any<int>(), Arg.Any<TempFileArtifactMeasurement>(), Arg.Any<CancellationToken>());
        f.Events.ShouldNotContain("facts");
        f.Events.ShouldNotContain("commit");
        foreach (var staged in f.Staged)
            await staged.Received(1).DisposeAsync();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Publish_CommittedJournalReplay_PrecedesPayloadCustodyAndTransactionAccess(bool terminal)
    {
        var f = new Fixture();
        if (terminal)
            f.Execution.ReadOutcomeAsync(f.Job, Arg.Any<CancellationToken>()).Returns(new MediaReconciliationJournalOutcome(f.Job, MediaReconciliationOutcomeKind.Completed, "done", 1, DateTimeOffset.UnixEpoch));
        else
            f.Execution.ReadAttemptOutcomeAsync(f.Job, 1, Arg.Any<CancellationToken>()).Returns(f.Outcome());
        (await f.Publication.PublishAsync(f.Attempt, null!, false)).IsError.ShouldBeFalse();
        f.Files.ReceivedCalls().ShouldBeEmpty();
        f.Store.ReceivedCalls().ShouldBeEmpty();
        f.Custody.ReceivedCalls().ShouldBeEmpty();
        f.Work.ReceivedCalls().ShouldBeEmpty();
    }
    [Fact]
    public async Task Publish_IndependentCustodyUnknown_PreservesPayloadAndAvoidsStaging()
    {
        var f = new Fixture();
        f.Custody.EnsureJobStoppedAsync(f.Job, Arg.Any<CancellationToken>()).Returns(MediaReconciliationErrors.Custody);
        (await f.Publication.PublishAsync(f.Attempt, f.Encoded, true)).FirstError.ShouldBe(MediaReconciliationErrors.Custody);
        f.Files.ReceivedCalls().ShouldBeEmpty();
        f.Transactions.ShouldBeEmpty();
        f.Encoded.Artifact.ReceivedCalls().ShouldNotContain(call => call.GetMethodInfo().Name == nameof(IAsyncDisposable.DisposeAsync));
    }
    [Fact]
    public async Task Publish_CdWithoutOptionalDatHashes_RetainsMeasuredArtifactChecksums()
    {
        var f = new Fixture();
        var measured = f.Encoded.ArtifactMeasurement;
        f.Encoded.Artifact.DatHashes.ShouldBeNull();
        f.Encoded.Artifact.DatHashes.Returns((DatHashResult?)null);
        f.Encoded.Artifact.ClearReceivedCalls();

        var result = await f.Publication.PublishAsync(f.Attempt, f.Encoded, true);

        result.IsError.ShouldBeFalse();
        await f.Files.Received(1).RecordArtifactChecksumsAsync(10,
            Arg.Is<TempFileArtifactMeasurement>(actual => actual.Sha256 == f.Encoded.Artifact.FileSha256 &&
                actual.Size == f.Encoded.Artifact.FileSize && actual.Md5 == measured.Md5 && actual.Sha1 == measured.Sha1),
            Arg.Any<CancellationToken>());
        f.Encoded.Artifact.ReceivedCalls().ShouldNotContain(call => call.GetMethodInfo().Name == "get_DatHashes");
        f.Transactions.ShouldHaveSingleItem().Commits.ShouldBe(1);
        foreach (var staged in f.Staged)
            await staged.Received(1).DisposeAsync();
    }
    [Theory]
    [InlineData("missing")]
    [InlineData("empty-md5")]
    [InlineData("empty-sha1")]
    [InlineData("wrong-sha256")]
    [InlineData("wrong-size")]
    public void EncodedCd_InvalidArtifactMeasurement_RejectsBeforeStaging(string defect)
    {
        var f = new Fixture();
        var actual = f.Encoded.ArtifactMeasurement;
        TempFileArtifactMeasurement invalid = defect switch
        {
            "missing" => null!,
            "empty-md5" => actual with { Md5 = default },
            "empty-sha1" => actual with { Sha1 = default },
            "wrong-sha256" => actual with { Sha256 = Sha256.FromBytes(System.Security.Cryptography.SHA256.HashData("other"u8)) },
            "wrong-size" => actual with { Size = actual.Size + 1 },
            _ => throw new ArgumentOutOfRangeException(nameof(defect)),
        };

        Should.Throw<ArgumentException>(() => new MediaReconciliationEncoded.Cd(f.Encoded.Artifact, invalid,
            f.Encoded.Measured, f.Encoded.Observation, f.Encoded.StoredOriginals, f.Encoded.ComparedAt));

        f.Files.ReceivedCalls().ShouldBeEmpty();
        f.Transactions.ShouldBeEmpty();
    }
    [Fact]
    public async Task Publish_Cd_StagesRawArtifactAndExactCueBeforeBeginningTheAtomicTransaction()
    {
        var f = new Fixture();
        (await f.Publication.PublishAsync(f.Attempt, f.Encoded, true)).IsError.ShouldBeFalse();
        f.Events.ShouldBe(["stage-raw", "stage-original", "begin", "prepare", "ledger", "facts", "commit", "dispose-transaction"]);
        await f.Files.Received(1).StageRawAsync(f.Encoded.Artifact, Arg.Any<CancellationToken>());
        await f.Files.Received(1).StageAsync(f.Encoded.StoredOriginals.Values.Single(), Arg.Any<CancellationToken>());
        await f.Store.Received(1).StageEncodedAsync(f.Attempt, f.Encoded, 10, Arg.Is<IReadOnlyList<RomRetainedDescriptor>>(rows => rows.Count == 1 && rows[0].Ordinal == 1 && rows[0].FileId == 11), Arg.Any<CancellationToken>());
        foreach (var staged in f.Staged)
            await staged.Received(1).DisposeAsync();
        f.Encoded.Artifact.ReceivedCalls().ShouldNotContain(call => call.GetMethodInfo().Name == nameof(IAsyncDisposable.DisposeAsync));
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Publish_DeadlockOrLockScopeExpansion_RestagesForAFreshTransactionUsingTheSameMeasuredInvocation(bool scopeExpansion)
    {
        var f = new Fixture();
        int attempts = 0;
        if (scopeExpansion)
            f.Store.PrepareAsync(f.Attempt, Arg.Any<IReadOnlyCollection<Sha256>>(), Arg.Any<CancellationToken>()).Returns(_ =>
                Task.FromResult<ErrorOr<Success>>(++attempts == 1 ? MediaReconciliationPublicationErrors.LockScopeChanged : Result.Success));
        else
        {
            var conflict = new InvalidOperationException("injected deadlock victim");
            f.Work.ConflictOf(conflict).Returns(TransactionConflict.Deadlock);
            f.Store.StageEncodedAsync(f.Attempt, f.Encoded, 10, Arg.Any<IReadOnlyList<RomRetainedDescriptor>>(), Arg.Any<CancellationToken>()).Returns(_ =>
                ++attempts == 1 ? Task.FromException<ErrorOr<Success>>(conflict) : Task.FromResult<ErrorOr<Success>>(Result.Success));
        }
        (await f.Publication.PublishAsync(f.Attempt, f.Encoded, true)).IsError.ShouldBeFalse();
        f.Transactions.Count.ShouldBe(2);
        f.Transactions[0].Commits.ShouldBe(0);
        f.Transactions[1].Commits.ShouldBe(1);
        f.Staged.Count.ShouldBe(4);
        f.Staged.Distinct().Count().ShouldBe(4);
        foreach (var staged in f.Staged)
            await staged.Received(1).DisposeAsync();
        await f.Files.Received(2).StageRawAsync(f.Encoded.Artifact, Arg.Any<CancellationToken>());
        f.Encoded.ComparedAt.ShouldBe(DateTimeOffset.UnixEpoch.AddTicks(100));
    }
    [Fact]
    public async Task Publish_LostCommitAcknowledgement_ReadsExactJournalWithoutRepeatingPublication()
    {
        var f = new Fixture();
        bool committed = false;
        f.OnCommit = () => { committed = true; throw new InvalidOperationException("injected lost response after commit"); };
        f.Execution.ReadAttemptOutcomeAsync(f.Job, 1, Arg.Any<CancellationToken>()).Returns(_ => Task.FromResult(committed ? f.Outcome() : null));
        (await f.Publication.PublishAsync(f.Attempt, f.Encoded, true)).IsError.ShouldBeFalse();
        f.Transactions.ShouldHaveSingleItem().Commits.ShouldBe(1);
        f.Staged.Count.ShouldBe(2);
        await f.Store.Received(1).StageEncodedAsync(f.Attempt, f.Encoded, 10, Arg.Any<IReadOnlyList<RomRetainedDescriptor>>(), Arg.Any<CancellationToken>());
    }
    [Fact]
    public async Task Publish_OriginalStagingFails_CleansEveryCompletedStageAndPreservesCallerOwnedPayload()
    {
        var f = new Fixture();
        var failure = new IOException("injected descriptor staging failure");
        f.Files.StageAsync(f.Encoded.StoredOriginals.Values.Single(), Arg.Any<CancellationToken>()).Returns(Task.FromException<IStagedFile>(failure));
        (await Should.ThrowAsync<IOException>(() => f.Publication.PublishAsync(f.Attempt, f.Encoded, true))).ShouldBeSameAs(failure);
        f.Transactions.ShouldBeEmpty();
        await f.Staged.ShouldHaveSingleItem().Received(1).DisposeAsync();
        f.Encoded.Artifact.ReceivedCalls().ShouldNotContain(call => call.GetMethodInfo().Name == nameof(IAsyncDisposable.DisposeAsync));
    }
    [Fact]
    public async Task Publish_RecordedDifferentFingerprint_RefusesWithoutTouchingPayloadOrCustody()
    {
        var f = new Fixture();
        f.Execution.ReadAttemptOutcomeAsync(f.Job, 1, Arg.Any<CancellationToken>()).Returns(f.Outcome() with { Fingerprint = DeliveryFacts.Hash('f') });
        (await f.Publication.PublishAsync(f.Attempt, null!, false)).FirstError.ShouldBe(MediaReconciliationErrors.Changed);
        f.Files.ReceivedCalls().ShouldBeEmpty();
        f.Custody.ReceivedCalls().ShouldBeEmpty();
        f.Transactions.ShouldBeEmpty();
    }
    [Theory]
    [InlineData("cancel")]
    [InlineData("fence")]
    [InlineData("io")]
    [InlineData("deadlock")]
    public async Task Publish_PrimaryAndStagingCleanupFail_PreservesExactPrimaryAndDoesNotRestage(string kind)
    {
        var f = new Fixture();
        using var cancellation = new CancellationTokenSource();
        Exception primary = kind switch
        {
            "cancel" => new OperationCanceledException("Original publication cancellation", cancellation.Token),
            "fence" => new JobExecutionOwnershipLostException(),
            "deadlock" => new InvalidOperationException("Original aborted deadlock"),
            _ => new IOException("Original publication I/O failure")
        };
        var secondary = new IOException("Secondary staging disposal failure");
        f.ConfigureStage = staged => staged.DisposeAsync().Returns(ValueTask.FromException(secondary));
        f.Work.ConflictOf(primary).Returns(kind == "deadlock" ? TransactionConflict.Deadlock : TransactionConflict.None);
        f.Store.StageEncodedAsync(f.Attempt, f.Encoded, 10, Arg.Any<IReadOnlyList<RomRetainedDescriptor>>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                if (kind == "cancel")
                    cancellation.Cancel();
                return Task.FromException<ErrorOr<Success>>(primary);
            });
        Exception? observed = null;

        try
        {
            await f.Publication.PublishAsync(f.Attempt, f.Encoded, true, cancellation.Token);
        }
        catch (Exception error)
        {
            observed = error;
        }

        observed.ShouldBeSameAs(primary);
        if (kind == "cancel")
            observed.ShouldBeOfType<OperationCanceledException>().CancellationToken.ShouldBe(cancellation.Token);
        f.Transactions.ShouldHaveSingleItem().Commits.ShouldBe(0);
        f.Staged.Count.ShouldBe(2);
        foreach (var staged in f.Staged)
            await staged.Received(1).DisposeAsync();
        var cleanup = primary.Data.Values.OfType<AggregateException>().ShouldHaveSingleItem();
        cleanup.InnerExceptions.Count.ShouldBe(2);
        cleanup.InnerExceptions.ShouldAllBe(error => ReferenceEquals(error, secondary));
        await f.Store.Received(1).StageEncodedAsync(f.Attempt, f.Encoded, 10,
            Arg.Any<IReadOnlyList<RomRetainedDescriptor>>(), Arg.Any<CancellationToken>());
        f.Encoded.Artifact.ReceivedCalls().ShouldNotContain(call => call.GetMethodInfo().Name == nameof(IAsyncDisposable.DisposeAsync));
    }

    [Theory]
    [InlineData("cancel", false)]
    [InlineData("cancel", true)]
    [InlineData("fence", false)]
    [InlineData("fence", true)]
    [InlineData("io", false)]
    [InlineData("io", true)]
    [InlineData("deadlock", false)]
    [InlineData("deadlock", true)]
    public async Task Publish_RecoveryReadFails_PreservesOriginalAndDoesNotBeginAnotherTransaction(string kind, bool attemptRead)
    {
        var f = new Fixture();
        using var cancellation = new CancellationTokenSource();
        Exception primary = kind switch
        {
            "cancel" => new OperationCanceledException("Original publication cancellation", cancellation.Token),
            "fence" => new JobExecutionOwnershipLostException(),
            "deadlock" => new InvalidOperationException("Original aborted deadlock"),
            _ => new IOException("Original publication I/O failure")
        };
        var secondary = new IOException("Journal recovery read unavailable");
        bool failed = false;
        f.Work.ConflictOf(primary).Returns(kind == "deadlock" ? TransactionConflict.Deadlock : TransactionConflict.None);
        f.Store.StageEncodedAsync(f.Attempt, f.Encoded, 10, Arg.Any<IReadOnlyList<RomRetainedDescriptor>>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                failed = true;
                if (kind == "cancel")
                    cancellation.Cancel();
                return Task.FromException<ErrorOr<Success>>(primary);
            });
        f.Execution.ReadOutcomeAsync(f.Job, Arg.Any<CancellationToken>()).Returns(_ => failed && !attemptRead
            ? Task.FromException<MediaReconciliationJournalOutcome?>(secondary)
            : Task.FromResult<MediaReconciliationJournalOutcome?>(null));
        f.Execution.ReadAttemptOutcomeAsync(f.Job, 1, Arg.Any<CancellationToken>()).Returns(_ => failed && attemptRead
            ? Task.FromException<MediaReconciliationAttemptOutcome?>(secondary)
            : Task.FromResult<MediaReconciliationAttemptOutcome?>(null));
        Exception? observed = null;

        try
        { await f.Publication.PublishAsync(f.Attempt, f.Encoded, true, cancellation.Token); }
        catch (Exception error) { observed = error; }

        observed.ShouldBeSameAs(primary);
        if (kind == "cancel")
            observed.ShouldBeOfType<OperationCanceledException>().CancellationToken.ShouldBe(cancellation.Token);
        primary.Data.Values.OfType<IOException>().ShouldContain(secondary);
        f.Transactions.ShouldHaveSingleItem().Commits.ShouldBe(0);
        f.Staged.Count.ShouldBe(2);
        foreach (var staged in f.Staged)
            await staged.Received(1).DisposeAsync();
        await f.Store.Received(1).StageEncodedAsync(f.Attempt, f.Encoded, 10,
            Arg.Any<IReadOnlyList<RomRetainedDescriptor>>(), Arg.Any<CancellationToken>());
        f.Encoded.Artifact.ReceivedCalls().ShouldNotContain(call => call.GetMethodInfo().Name == nameof(IAsyncDisposable.DisposeAsync));
    }

    [Fact]
    public async Task Publish_CleanupFailsAfterCommit_ReportsAllFailuresAndKeepsTheCommittedJournalReplayable()
    {
        var f = new Fixture();
        bool committed = false;
        f.OnCommit = () => committed = true;
        f.Execution.ReadAttemptOutcomeAsync(f.Job, 1, Arg.Any<CancellationToken>()).Returns(_ => Task.FromResult(committed ? f.Outcome() : null));
        f.ConfigureStage = staged => staged.DisposeAsync().Returns(ValueTask.FromException(new IOException("injected staged cleanup failure")));
        var failure = await Should.ThrowAsync<AggregateException>(() => f.Publication.PublishAsync(f.Attempt, f.Encoded, true));
        failure.InnerExceptions.Count.ShouldBe(2);
        f.Transactions.ShouldHaveSingleItem().Commits.ShouldBe(1);
        foreach (var staged in f.Staged)
            staged.ReceivedCalls().Count(call => call.GetMethodInfo().Name == nameof(IAsyncDisposable.DisposeAsync)).ShouldBe(1);
        (await f.Publication.PublishAsync(f.Attempt, null!, false)).IsError.ShouldBeFalse();
        f.Transactions.Count.ShouldBe(1);
        f.Staged.Count.ShouldBe(2);
    }
    [Fact]
    public async Task Release_StoppedCanonicalAttempt_PreparesAndCommitsWithoutStagingFiles()
    {
        var f = new Fixture();
        var captured = f.Attempt.Capture;
        var attempt = new MediaReconciliationAttempt(1, new(f.Job, 1, captured.Sources, MediaReconciliationOperation.Release, captured.Identity.Recipe,
            DeliveryFacts.Hash('a'), DeliveryFacts.Hash('d'), captured.Decoder, captured.KindId, null, null), null);
        f.Store.PrepareAsync(attempt, Arg.Any<IReadOnlyCollection<Sha256>>(), Arg.Any<CancellationToken>()).Returns(Result.Success);
        f.Store.StageReleaseAsync(attempt, 42, f.Encoded.Measured.Measurement.Id, Arg.Any<CancellationToken>()).Returns(Result.Success);
        (await f.Publication.ReleaseAsync(attempt, 42, f.Encoded.Measured.Measurement.Id, true)).IsError.ShouldBeFalse();
        f.Files.ReceivedCalls().ShouldBeEmpty();
        f.Transactions.ShouldHaveSingleItem().Commits.ShouldBe(1);
        await f.Store.Received(1).StageReleaseAsync(attempt, 42, f.Encoded.Measured.Measurement.Id, Arg.Any<CancellationToken>());
    }
    [Theory]
    [InlineData("valid")]
    [InlineData("scope-change")]
    [InlineData("deadlock")]
    [InlineData("lost-ack")]
    public async Task Adopt_AtomicPublicationOrTransactionRecovery_ReusesTheExactStoppedComparisonWithoutCreation(string failure)
    {
        var f = new Fixture();
        var original = (MediaReconciliationLayout.Original)f.Attempt.Capture.Sources.Layout!;
        var graph = f.Attempt.Capture.Sources.Inputs.OfType<MediaReconciliationInput.Logical>().Single().Graph;
        var cue = ((RomReconstructionManifest.CdTracks)graph.Manifest).Members.OfType<RomCdStoredMember>().Single();
        var witness = new MediaReconciliationLayout.Catalog(7, Guid.NewGuid(), original.PlatformId,
            new(13, original.ReleaseId, original.CatalogFingerprint, "cue-fingerprint", cue.Path, cue.Identity.Size, cue.Identity.Sha1, cue.Identity.Md5, cue.Identity.Crc32), "formatter");
        var sources = new MediaReconciliationSources([.. f.Attempt.Capture.Sources.Inputs, new MediaReconciliationInput.Artifact(graph, null, witness)], original, [], null);
        var capture = f.Attempt.Capture;
        var attempt = new MediaReconciliationAttempt(1, new(f.Job, 1, sources, MediaReconciliationOperation.Adopt, capture.Identity.Recipe,
            DeliveryFacts.Hash('a'), capture.Identity.LimitsFingerprint, capture.Decoder, capture.KindId, null, null), null);
        var proof = graph.Verifications.Single();
        var measured = new RomMeasuredRepresentation(graph.Container, graph.Manifest, new(Guid.NewGuid(), graph.ArtifactSha256, graph.ArtifactSize, proof.Decoder,
            "chd-cd-whole-files/v1", "independent-comparer", proof.ObservedAt, proof.Outputs, graph.Container, proof.ComputedRawSha1, proof.ComputedOverallSha1));
        var hashes = f.Encoded.ArtifactMeasurement;
        var adopted = new MediaReconciliationAdopted(f.Encoded.Artifact, new(measured, "inspector", null,
            new(graph.ArtifactSha256, hashes.Md5, hashes.Sha1, graph.ArtifactSize)), f.Encoded.StoredOriginals);
        int prepares = 0, publications = 0;
        bool committed = false;
        var deadlock = new InvalidOperationException("injected adoption deadlock");
        f.Work.ConflictOf(deadlock).Returns(TransactionConflict.Deadlock);
        f.Store.PrepareAsync(attempt, Arg.Any<IReadOnlyCollection<Sha256>>(), Arg.Any<CancellationToken>()).Returns(_ =>
        {
            f.Events.Add("prepare");
            prepares++;
            return Task.FromResult<ErrorOr<Success>>(failure == "scope-change" && prepares == 1 ? MediaReconciliationPublicationErrors.LockScopeChanged : Result.Success);
        });
        f.Store.StageAdoptedAsync(attempt, adopted, 10, Arg.Any<IReadOnlyList<RomRetainedDescriptor>>(), Arg.Any<CancellationToken>()).Returns(_ =>
        {
            f.Events.Add("adopt-facts");
            publications++;
            if (failure == "deadlock" && publications == 1)
                throw deadlock;
            return Task.FromResult<ErrorOr<Success>>(Result.Success);
        });
        if (failure == "lost-ack")
        {
            f.OnCommit = () => { committed = true; throw new IOException("lost adoption commit acknowledgement"); };
            f.Execution.ReadAttemptOutcomeAsync(f.Job, 1, Arg.Any<CancellationToken>()).Returns(_ => Task.FromResult<MediaReconciliationAttemptOutcome?>(committed
                ? new(f.Job, 1, attempt.Capture.Identity.Fingerprint, MediaReconciliationAttemptDisposition.Recorded, "adopted", 42, measured.Measurement.Id, null, null, DateTimeOffset.UtcNow) : null));
        }
        (await f.Publication.AdoptAsync(attempt, adopted, processTreeStopped: true)).IsError.ShouldBeFalse();
        f.Transactions.Sum(transaction => transaction.Commits).ShouldBe(1);
        publications.ShouldBe(failure == "deadlock" ? 2 : 1);
        prepares.ShouldBe(failure is "scope-change" or "deadlock" ? 2 : 1);
        foreach (var staged in f.Staged)
            await staged.Received(1).DisposeAsync();
        await f.Store.DidNotReceiveWithAnyArgs().StageEncodedAsync(default!, default!, default, default!, default);
        f.Encoded.Artifact.ReceivedCalls().ShouldNotContain(call => call.GetMethodInfo().Name == nameof(IAsyncDisposable.DisposeAsync));
        if (failure == "valid")
            f.Events.ShouldBe(["stage-raw", "stage-original", "begin", "prepare", "ledger", "adopt-facts", "commit", "dispose-transaction"]);
    }
    private sealed class Fixture
    {
        internal Guid Job { get; } = Guid.NewGuid();
        internal IFileStorageService Files { get; } = Substitute.For<IFileStorageService>();
        internal IUnitOfWork Work { get; } = Substitute.For<IUnitOfWork>();
        internal IMediaReconciliationExecution Execution { get; } = Substitute.For<IMediaReconciliationExecution>();
        internal IMediaReconciliationPublicationStore Store { get; } = Substitute.For<IMediaReconciliationPublicationStore>();
        internal IMediaRunCustody Custody { get; } = Substitute.For<IMediaRunCustody>();
        internal List<string> Events { get; } = [];
        internal List<IStagedFile> Staged { get; } = [];
        internal List<Transaction> Transactions { get; } = [];
        internal Action<IStagedFile> ConfigureStage { get; set; } = _ => { };
        internal Action OnCommit { get; set; } = () => { };
        internal bool PendingFiles { get; set; }
        internal MediaReconciliationAttempt Attempt { get; }
        internal MediaReconciliationEncoded.Cd Encoded { get; }
        internal MediaReconciliationPublication Publication { get; }
        internal MediaReconciliationAttemptOutcome Outcome() => new(Job, 1, Attempt.Capture.Identity.Fingerprint, MediaReconciliationAttemptDisposition.Recorded, "encoded", 42, Encoded.Measured.Measurement.Id, Encoded.Observation.Id, null, Encoded.ComparedAt);
        internal Fixture()
        {
            var graph = DeliveryFacts.CdContent().Preferred.Facts;
            var measured = new RomMeasuredRepresentation(graph.Container, graph.Manifest, graph.Verifications.Single());
            var encoder = new RomChdEncoderBuild("linux-x64", "mame0280", DeliveryFacts.Hash('e'), 1);
            var recipe = RecipeId.Parse("fixture-cd/v1");
            var artifact = Substitute.For<ITempFile>();
            artifact.FileSha256.Returns(graph.ArtifactSha256);
            artifact.FileSize.Returns(graph.ArtifactSize);
            var artifactMeasurement = new TempFileArtifactMeasurement(graph.ArtifactSha256, DeliveryFacts.Md5Value('b'), DeliveryFacts.Short('b'), graph.ArtifactSize);
            var cd = (RomReconstructionManifest.CdTracks)graph.Manifest;
            var member = cd.Members.OfType<RomCdStoredMember>().Single();
            var cue = Substitute.For<ITempFile>();
            cue.FileSha256.Returns(member.Identity.Sha256);
            cue.FileSize.Returns(member.Identity.Size);
            var boot = new RomPs1CdBoot(RomPs1CdBoot.CurrentProfile, 0, 64, 24, 864, "SLUS_123.45", 26, 4096, 0x80010000, 0x80010000, 2048);
            Encoded = new(artifact, artifactMeasurement, measured, new(measured, recipe, encoder, DeliveryFacts.Hash('e'), 100, "fixture-inspector", boot),
                new Dictionary<string, ITempFile> { [member.Path] = cue }, DateTimeOffset.UnixEpoch.AddTicks(100));
            var sources = new MediaReconciliationSources([new MediaReconciliationInput.Logical(MediaReconciliationSourceRole.Bin,"data.bin",graph,graph.Outputs.Single().Id,graph.Verifications.Single().Id),
                new MediaReconciliationInput.Stored(MediaReconciliationSourceRole.Cue,"disc.cue",graph.Descriptors.Single().FileId,member.Identity)], new MediaReconciliationLayout.Original(1, Guid.NewGuid(), 1, 1, "catalog", "disc.cue"), [], null);
            Attempt = new(1, new(Job, 1, sources, MediaReconciliationOperation.Encode, recipe, DeliveryFacts.Hash('a'), DeliveryFacts.Hash('d'), measured.Measurement.Decoder, "ps1-cd-set", encoder.BuildLabel, null), null);
            Custody.EnsureJobStoppedAsync(Job, Arg.Any<CancellationToken>()).Returns(Result.Success);
            Files.StageRawAsync(artifact, Arg.Any<CancellationToken>()).Returns(_ => { Events.Add("stage-raw"); return Stage(artifact); });
            Files.StageAsync(cue, Arg.Any<CancellationToken>()).Returns(_ => { Events.Add("stage-original"); return Stage(cue); });
            Work.BeginTransactionAsync(Arg.Any<CancellationToken>()).Returns(_ =>
            {
                Events.Add("begin");
                var transaction = new Transaction(Events, () => OnCommit());
                Transactions.Add(transaction);
                return Task.FromResult<ITransaction>(transaction);
            });
            Store.PrepareAsync(Attempt, Arg.Any<IReadOnlyCollection<Sha256>>(), Arg.Any<CancellationToken>()).Returns(_ => { Events.Add("prepare"); return Task.FromResult<ErrorOr<Success>>(Result.Success); });
            Work.When(value => value.FlushAsync(Arg.Any<CancellationToken>())).Do(_ => Events.Add("flush"));
            Files.PublishStagedAsync(Arg.Any<IReadOnlyList<IStagedFile>>(), Arg.Any<CancellationToken>()).Returns(call =>
            {
                Events.Add("ledger");
                return Task.FromResult<IStagedFilePublicationBatch>(new PublicationBatch(PendingFiles,
                    call.Arg<IReadOnlyList<IStagedFile>>().Select((file, index) => new FileStoreResult(
                        FileEntity.Rehydrate(10 + index, file.Sha256, file.Size, file.Size, false, DateTimeOffset.UnixEpoch), true, false)).ToArray()));
            });
            Store.StageEncodedAsync(Attempt, Encoded, 10, Arg.Any<IReadOnlyList<RomRetainedDescriptor>>(), Arg.Any<CancellationToken>()).Returns(_ => { Events.Add("facts"); return Task.FromResult<ErrorOr<Success>>(Result.Success); });
            Publication = new(Files, Work, Store, Execution, Custody);
        }
        private IStagedFile Stage(ITempFile file)
        {
            var hash = file.FileSha256;
            long size = file.FileSize;
            var staged = Substitute.For<IStagedFile>();
            staged.Sha256.Returns(hash);
            staged.Size.Returns(size);
            ConfigureStage(staged);
            Staged.Add(staged);
            return staged;
        }
    }
    private sealed record PublicationBatch(bool RequiresFlush, IReadOnlyList<FileStoreResult> Results) : IStagedFilePublicationBatch
    {
        public IReadOnlyList<FileStoreResult> ReadAfterFlush() => Results;
    }
    private sealed class Transaction(List<string> events, Action commit) : ITransaction
    {
        internal int Commits { get; private set; }
        public Task CommitAsync(CancellationToken cancellationToken = default) { Commits++; events.Add("commit"); commit(); return Task.CompletedTask; }
        public Task RollbackAsync(CancellationToken cancellationToken = default) { events.Add("rollback"); return Task.CompletedTask; }
        public ValueTask DisposeAsync() { events.Add("dispose-transaction"); return ValueTask.CompletedTask; }
    }
}
