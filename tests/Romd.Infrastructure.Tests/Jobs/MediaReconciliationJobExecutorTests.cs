using ErrorOr;
using NSubstitute;
using Romd.Admin.Application.Ingestion.Jobs;
using Romd.Admin.Application.Source.Rom.Reconciliation;
using Romd.Admin.Application.Storage.Files;
using Romd.Application.Common.Content;
using Romd.Domain.Hashing;
using Romd.Domain.Jobs;
using Romd.Domain.Source.Rom;
using Romd.Infrastructure.Jobs.Executors;
using Romd.Infrastructure.Jobs.Lifecycle;
using Romd.Infrastructure.Storage;
using Romd.PostgreSql.TestSupport;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Jobs;

public sealed class MediaReconciliationJobExecutorTests
{
    [Fact]
    public async Task Execute_CommittedTerminalJournal_WinsBeforeOwnershipPlanningAndPayloadAccess()
    {
        var f = new Fixture(MediaReconciliationOperation.Verify);
        f.Execution.ReadOutcomeAsync(f.Job.Id, Arg.Any<CancellationToken>()).Returns(f.Outcome(MediaReconciliationOutcomeKind.Completed));
        var lost = new JobContext(null, _ => Task.CompletedTask, CancellationToken.None, _ => Task.FromResult(false));
        await f.Executor.ExecuteAsync(f.Job, lost);
        f.Job.IsTerminal.ShouldBeTrue();
        f.Planning.ReceivedCalls().ShouldBeEmpty();
        f.Media.ReceivedCalls().ShouldBeEmpty();
        await f.Execution.DidNotReceiveWithAnyArgs().CaptureAsync(default!);
    }

    [Theory]
    [InlineData(MediaReconciliationOperation.Verify, false)]
    [InlineData(MediaReconciliationOperation.Inspect, true)]
    [InlineData(MediaReconciliationOperation.Inspect, false)]
    public async Task Execute_IndependentMeasurement_ReleasesProcessingBeforeAtomicFactPublication(MediaReconciliationOperation operation, bool original)
    {
        var f = new Fixture(operation, original);
        bool released = false, published = false;
        f.VerifyLease.ReleaseProcessingAsync(Arg.Any<CancellationToken>()).Returns(_ => { released = true; return Result.Success; });
        f.OriginalLease.ReleaseProcessingAsync(Arg.Any<CancellationToken>()).Returns(_ => { released = true; return Result.Success; });
        f.Execution.RecordVerificationAsync(Arg.Any<MediaReconciliationAttempt>(), f.Measurement, true, Arg.Any<CancellationToken>()).Returns(call =>
        {
            released.ShouldBeTrue();
            call.Arg<MediaReconciliationAttempt>().Capture.RequestedVersion.ShouldBe(1);
            published = true;
            return Result.Success;
        });
        await f.Executor.ExecuteAsync(f.Job, f.Context);
        published.ShouldBeTrue();
        f.Job.Outcome.ShouldBe(MediaReconciliationOutcomeKind.Completed);
        f.Context.DeferredUntil.ShouldBeNull();
        await f.Execution.Received(1).CaptureAsync(f.Capture, Arg.Any<CancellationToken>());
        if (original)
        {
            await f.OriginalLease.Received(1).InspectAsync(Arg.Any<CancellationToken>());
            f.Media.ReceivedCalls().ShouldBeEmpty();
        }
        else
        { await f.VerifyLease.Received(1).VerifyAsync(Arg.Any<CancellationToken>()); f.Originals.ReceivedCalls().ShouldBeEmpty(); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Execute_UnreleasedProcessingOrUnknownCustody_NeverPublishesFailsOrAcknowledgesSources(bool unknown)
    {
        var f = new Fixture(MediaReconciliationOperation.Verify);
        f.VerifyLease.ProcessTreeStopped.Returns(!unknown);
        f.VerifyLease.ReleaseProcessingAsync(Arg.Any<CancellationToken>()).Returns(RomMediaVerificationErrors.Custody);
        await f.Executor.ExecuteAsync(f.Job, f.Context);
        f.Context.DeferredUntil.ShouldNotBeNull();
        f.Job.IsTerminal.ShouldBeFalse();
        await f.Execution.DidNotReceiveWithAnyArgs().RecordVerificationAsync(default!, default!, default);
        await f.Execution.DidNotReceiveWithAnyArgs().FailAttemptAsync(default!, default!, default, default);
        await f.Execution.DidNotReceiveWithAnyArgs().FinishAsync(default, default, default, default!, default);
        if (unknown)
            await f.VerifyLease.DidNotReceiveWithAnyArgs().ReleaseProcessingAsync();
    }

    [Fact]
    public async Task Execute_LostPublicationResponse_RecoversExactAttemptBeforeReplanningWithoutRepeatingTheDecoder()
    {
        var f = new Fixture(MediaReconciliationOperation.Verify);
        f.Execution.RecordVerificationAsync(Arg.Any<MediaReconciliationAttempt>(), Arg.Any<RomMediaVerificationMeasurement>(), true, Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromException<ErrorOr<Success>>(new IOException("commit response lost")));
        f.Execution.ReadAttemptOutcomeAsync(f.Job.Id, 1, Arg.Any<CancellationToken>()).Returns(new MediaReconciliationAttemptOutcome(f.Job.Id, 1, f.Capture.Identity.Fingerprint,
            MediaReconciliationAttemptDisposition.Recorded, "verified", f.Graph.Id, f.Measurement.Representation.Measurement.Id, null, null, DateTimeOffset.UtcNow));
        await f.Executor.ExecuteAsync(f.Job, f.Context);
        f.Job.Outcome.ShouldBe(MediaReconciliationOutcomeKind.Completed);
        await f.VerifyLease.Received(1).VerifyAsync(Arg.Any<CancellationToken>());
        await f.Execution.Received(1).ReadAttemptOutcomeAsync(f.Job.Id, 1, Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("cancel", false)]
    [InlineData("cancel", true)]
    [InlineData("fence", false)]
    [InlineData("fence", true)]
    [InlineData("io", false)]
    [InlineData("io", true)]
    public async Task Execute_MutationAndRecoveryReadFail_PreservesPrimaryWithoutRepeatingMeasurement(string kind, bool attemptRead)
    {
        var f = new Fixture(MediaReconciliationOperation.Verify);
        using var cancellation = new CancellationTokenSource();
        Exception primary = kind switch
        {
            "cancel" => new OperationCanceledException("Original mutation cancellation", cancellation.Token),
            "fence" => new JobExecutionOwnershipLostException(),
            _ => new IOException("Original mutation acknowledgement failure")
        };
        var secondary = new IOException("Journal recovery read unavailable");
        bool failed = false;
        f.Execution.RecordVerificationAsync(Arg.Any<MediaReconciliationAttempt>(), Arg.Any<RomMediaVerificationMeasurement>(), true, Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                failed = true;
                if (kind == "cancel")
                    cancellation.Cancel();
                return Task.FromException<ErrorOr<Success>>(primary);
            });
        f.Execution.ReadOutcomeAsync(f.Job.Id, Arg.Any<CancellationToken>()).Returns(_ => failed && !attemptRead
            ? Task.FromException<MediaReconciliationJournalOutcome?>(secondary)
            : Task.FromResult<MediaReconciliationJournalOutcome?>(null));
        f.Execution.ReadAttemptOutcomeAsync(f.Job.Id, 1, Arg.Any<CancellationToken>()).Returns(_ => failed && attemptRead
            ? Task.FromException<MediaReconciliationAttemptOutcome?>(secondary)
            : Task.FromResult<MediaReconciliationAttemptOutcome?>(null));
        var context = new JobContext(null, _ => Task.CompletedTask, cancellation.Token);
        Exception? observed = null;

        try
        { await f.Executor.ExecuteAsync(f.Job, context); }
        catch (Exception error) { observed = error; }

        observed.ShouldBeSameAs(primary);
        if (kind == "cancel")
            observed.ShouldBeOfType<OperationCanceledException>().CancellationToken.ShouldBe(cancellation.Token);
        primary.Data.Values.OfType<IOException>().ShouldContain(secondary);
        f.Job.Errors.ShouldBeEmpty();
        f.Job.IsTerminal.ShouldBeFalse();
        context.DeferredUntil.ShouldBeNull();
        await f.VerifyLease.Received(1).VerifyAsync(Arg.Any<CancellationToken>());
        await f.VerifyLease.Received(1).DisposeAsync();
        await f.Execution.Received(1).RecordVerificationAsync(Arg.Any<MediaReconciliationAttempt>(),
            Arg.Any<RomMediaVerificationMeasurement>(), true, Arg.Any<CancellationToken>());
        await f.Execution.DidNotReceiveWithAnyArgs().FinishAsync(default, default, default, default!, default);
    }

    [Theory]
    [InlineData("cancel")]
    [InlineData("fence")]
    [InlineData("io")]
    public async Task Execute_TerminalCommitAndRecoveryReadFail_PreservesOriginalWithoutAnotherFinish(string kind)
    {
        var f = new Fixture(MediaReconciliationOperation.Verify);
        using var cancellation = new CancellationTokenSource();
        Exception primary = kind switch
        {
            "cancel" => new OperationCanceledException("Original terminal cancellation", cancellation.Token),
            "fence" => new JobExecutionOwnershipLostException(),
            _ => new IOException("Original terminal acknowledgement failure")
        };
        var secondary = new IOException("Terminal recovery read unavailable");
        bool failed = false;
        f.Planning.NextAsync(f.Job.Id, Arg.Any<MediaReconciliationDemandState>(), Arg.Any<CancellationToken>())
            .Returns((ErrorOr<MediaReconciliationDirective>)new MediaReconciliationDirective.Settle(MediaReconciliationOutcomeKind.Completed, "complete"));
        f.Execution.FinishAsync(f.Job.Id, 1, MediaReconciliationOutcomeKind.Completed, "complete", true, Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                failed = true;
                if (kind == "cancel")
                    cancellation.Cancel();
                return Task.FromException<ErrorOr<MediaReconciliationJournalOutcome>>(primary);
            });
        f.Execution.ReadOutcomeAsync(f.Job.Id, Arg.Any<CancellationToken>()).Returns(_ => failed
            ? Task.FromException<MediaReconciliationJournalOutcome?>(secondary)
            : Task.FromResult<MediaReconciliationJournalOutcome?>(null));
        var context = new JobContext(null, _ => Task.CompletedTask, cancellation.Token);
        Exception? observed = null;

        try
        { await f.Executor.ExecuteAsync(f.Job, context); }
        catch (Exception error) { observed = error; }

        observed.ShouldBeSameAs(primary);
        if (kind == "cancel")
            observed.ShouldBeOfType<OperationCanceledException>().CancellationToken.ShouldBe(cancellation.Token);
        primary.Data.Values.OfType<IOException>().ShouldContain(secondary);
        f.Job.Errors.ShouldBeEmpty();
        f.Job.IsTerminal.ShouldBeFalse();
        context.DeferredUntil.ShouldBeNull();
        await f.Execution.Received(1).FinishAsync(f.Job.Id, 1, MediaReconciliationOutcomeKind.Completed, "complete", true, Arg.Any<CancellationToken>());
        f.Media.ReceivedCalls().ShouldBeEmpty();
        await f.Execution.DidNotReceiveWithAnyArgs().CaptureAsync(default!);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Execute_CancellationRecoverySignalOrDemandReadFails_KeepsOriginalTokenAndDoesNotSettle(bool demandRead)
    {
        var f = new Fixture(MediaReconciliationOperation.Verify);
        using var cancellation = new CancellationTokenSource();
        var primary = new OperationCanceledException("Original measurement cancellation", cancellation.Token);
        var secondary = new IOException("Cancellation recovery read unavailable");
        bool failed = false;
        f.VerifyLease.VerifyAsync(Arg.Any<CancellationToken>()).Returns(_ =>
        {
            failed = true;
            cancellation.Cancel();
            return Task.FromException<ErrorOr<RomMediaVerificationMeasurement>>(primary);
        });
        f.Execution.IsCancellationRequestedAsync(f.Job.Id, Arg.Any<CancellationToken>()).Returns(_ => demandRead
            ? Task.FromResult(true) : Task.FromException<bool>(secondary));
        f.Execution.ReadDemandAsync(f.Job.Id, Arg.Any<CancellationToken>()).Returns(_ => failed
            ? Task.FromException<MediaReconciliationDemandState?>(secondary)
            : Task.FromResult<MediaReconciliationDemandState?>(new(f.Subject, 1, false, false)));
        var context = new JobContext(null, _ => Task.CompletedTask, cancellation.Token);
        Exception? observed = null;

        try
        { await f.Executor.ExecuteAsync(f.Job, context); }
        catch (Exception error) { observed = error; }

        observed.ShouldBeSameAs(primary);
        observed.ShouldBeOfType<OperationCanceledException>().CancellationToken.ShouldBe(cancellation.Token);
        primary.Data.Values.OfType<IOException>().ShouldContain(secondary);
        f.Job.Errors.ShouldBeEmpty();
        f.Job.IsTerminal.ShouldBeFalse();
        context.DeferredUntil.ShouldBeNull();
        await f.VerifyLease.Received(1).VerifyAsync(Arg.Any<CancellationToken>());
        await f.Execution.DidNotReceiveWithAnyArgs().RecordVerificationAsync(default!, default!, default);
        await f.Execution.DidNotReceiveWithAnyArgs().FinishAsync(default, default, default, default!, default);
    }

    [Fact]
    public async Task Execute_UnrelatedAttemptJournal_CannotMaskAnAmbiguousPublicationFailure()
    {
        var f = new Fixture(MediaReconciliationOperation.Verify);
        f.Execution.RecordVerificationAsync(Arg.Any<MediaReconciliationAttempt>(), Arg.Any<RomMediaVerificationMeasurement>(), true, Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromException<ErrorOr<Success>>(new IOException("commit response lost")));
        f.Execution.ReadAttemptOutcomeAsync(f.Job.Id, 1, Arg.Any<CancellationToken>()).Returns(new MediaReconciliationAttemptOutcome(f.Job.Id, 1, Hash('f'),
            MediaReconciliationAttemptDisposition.Recorded, "verified", f.Graph.Id, f.Measurement.Representation.Measurement.Id, null, null, DateTimeOffset.UtcNow));
        await Should.ThrowAsync<IOException>(() => f.Executor.ExecuteAsync(f.Job, f.Context));
        f.Job.IsTerminal.ShouldBeFalse();
        await f.VerifyLease.Received(1).VerifyAsync(Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Execute_DeterministicOrCachedFailure_StopsExactAttemptAndSettlesWithoutRepeatingPayload(bool cached)
    {
        var f = new Fixture(MediaReconciliationOperation.Verify);
        if (cached)
            f.Execution.CaptureAsync(f.Capture, Arg.Any<CancellationToken>()).Returns(new MediaReconciliationAttempt(1, f.Capture, new(Guid.NewGuid(), 3, "fixture-invalid")));
        else
            f.VerifyLease.VerifyAsync(Arg.Any<CancellationToken>()).Returns(Error.Validation("fixture-invalid", "corrupt artifact"));
        await f.Executor.ExecuteAsync(f.Job, f.Context);
        f.Job.Outcome.ShouldBe(MediaReconciliationOutcomeKind.Failed);
        f.Job.OutcomeCode.ShouldBe("fixture-invalid");
        await f.Execution.Received(1).FailAttemptAsync(Arg.Is<MediaReconciliationAttempt>(a => a.Capture == f.Capture), "fixture-invalid", true, true, Arg.Any<CancellationToken>());
        if (cached)
            f.Media.ReceivedCalls().ShouldBeEmpty();
        else
            await f.VerifyLease.Received(1).ReleaseProcessingAsync(Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(MediaReconciliationOperation.Verify, "Rom.Media.Startup")]
    [InlineData(MediaReconciliationOperation.Encode, "Rom.Media.Startup")]
    [InlineData(MediaReconciliationOperation.Verify, "Rom.Media.Crash")]
    [InlineData(MediaReconciliationOperation.Encode, "Rom.Media.Crash")]
    [InlineData(MediaReconciliationOperation.Verify, "Rom.Media.Protocol")]
    [InlineData(MediaReconciliationOperation.Encode, "Rom.Media.Protocol")]
    [InlineData(MediaReconciliationOperation.Verify, "Rom.Media.IoFailure")]
    [InlineData(MediaReconciliationOperation.Encode, "Rom.Media.IoFailure")]
    [InlineData(MediaReconciliationOperation.Verify, "Rom.Media.OutputLimit")]
    [InlineData(MediaReconciliationOperation.Encode, "Rom.Media.OutputLimit")]
    [InlineData(MediaReconciliationOperation.Verify, "Rom.Media.CpuDeadline")]
    [InlineData(MediaReconciliationOperation.Encode, "Rom.Media.CpuDeadline")]
    [InlineData(MediaReconciliationOperation.Verify, "Rom.Media.WallDeadline")]
    [InlineData(MediaReconciliationOperation.Encode, "Rom.Media.WallDeadline")]
    [InlineData(MediaReconciliationOperation.Verify, "Temp.SourceIdentity")]
    [InlineData(MediaReconciliationOperation.Encode, "Temp.SourceIdentity")]
    public async Task Execute_TransientWorkerFailure_StopsAttemptWithoutCachingOrAcknowledgingDemand(MediaReconciliationOperation operation, string code)
    {
        var f = new Fixture(operation);
        int progress = 0;
        if (operation == MediaReconciliationOperation.Verify)
            f.VerifyLease.VerifyAsync(Arg.Any<CancellationToken>()).Returns(Error.Validation(code, "transient execution failure"));
        else
        {
            var lease = Substitute.For<IMediaReconciliationEncodingLease>();
            lease.ProcessTreeStopped.Returns(true);
            lease.EncodeAndVerifyAsync(Arg.Any<Func<CancellationToken, Task>>(), Arg.Any<CancellationToken>()).Returns(Error.Validation(code, "transient execution failure"));
            f.Encoding.AcquireAsync(Arg.Any<MediaReconciliationAttempt>(), Arg.Any<CancellationToken>()).Returns(ErrorOrFactory.From(lease));
        }
        f.Execution.FailAttemptAsync(Arg.Any<MediaReconciliationAttempt>(), code, false, true, Arg.Any<CancellationToken>()).Returns(Result.Success);
        var context = new JobContext(null, _ => Task.CompletedTask, CancellationToken.None, onUploadProgress: () => progress++);
        await f.Executor.ExecuteAsync(f.Job, context);
        context.DeferredUntil.ShouldNotBeNull();
        f.Job.IsTerminal.ShouldBeFalse();
        progress.ShouldBe(0);
        await f.Execution.Received(1).FailAttemptAsync(Arg.Any<MediaReconciliationAttempt>(), code, false, true, Arg.Any<CancellationToken>());
        await f.Execution.DidNotReceiveWithAnyArgs().FinishAsync(default, default, default, default!, default);
    }

    [Fact]
    public async Task Execute_UnmeasuredIdentityOriginal_SettlesWithoutDeferralOrNativeAdmission()
    {
        var f = new Fixture(MediaReconciliationOperation.Inspect, original: true);
        var graph = new RomRepresentationFacts(f.Graph.Id, f.Graph.FileId, f.Graph.ArtifactSha256, f.Graph.ArtifactSize,
            RomContainerFacts.Identity.Instance, f.Graph.Manifest, f.Graph.Outputs, [], []);
        var output = graph.Outputs.Single();
        var content = new RomContentDescriptor(output.RomId!.Value, output.Id, output.Identity, "disc.iso",
            [new RomContentArtifact(graph, output.Id, isPreferred: true)], [], []);
        var snapshots = Substitute.For<IMediaReconciliationSnapshotReader>();
        snapshots.ReadAsync(f.Subject, Arg.Any<CancellationToken>())
            .Returns((ErrorOr<MediaReconciliationFacts>)new MediaReconciliationFacts.Logical(content, []));
        var composition = RomMediaPolicyComposition.Create();
        var planning = new MediaReconciliationPlanning(snapshots, new MediaReconciliationDecision(composition.Kinds,
            composition.Reconciliation, Substitute.For<IMediaHostCapabilities>(), Substitute.For<IMediaReconciliationMediaProfile>()));
        f.Planning.NextAsync(f.Job.Id, Arg.Any<MediaReconciliationDemandState>(), Arg.Any<CancellationToken>())
            .Returns(call => planning.NextAsync(f.Job.Id, call.Arg<MediaReconciliationDemandState>(), call.Arg<CancellationToken>()));
        f.Execution.FinishAsync(f.Job.Id, 1, MediaReconciliationOutcomeKind.Completed, "source-unverified", true, Arg.Any<CancellationToken>())
            .Returns(f.Outcome(MediaReconciliationOutcomeKind.Completed, code: "source-unverified"));

        await f.Executor.ExecuteAsync(f.Job, f.Context);

        f.Job.IsTerminal.ShouldBeTrue();
        f.Job.Outcome.ShouldBe(MediaReconciliationOutcomeKind.Completed);
        f.Job.OutcomeCode.ShouldBe("source-unverified");
        f.Context.DeferredUntil.ShouldBeNull();
        await f.Execution.Received(1).SupersedeAttemptAsync(f.Job.Id, "no-further-work", true, Arg.Any<CancellationToken>());
        await f.Execution.Received(1).FinishAsync(f.Job.Id, 1, MediaReconciliationOutcomeKind.Completed, "source-unverified", true, Arg.Any<CancellationToken>());
        await f.Execution.DidNotReceiveWithAnyArgs().CaptureAsync(default!, default);
        f.Originals.ReceivedCalls().ShouldBeEmpty();
        f.Media.ReceivedCalls().ShouldBeEmpty();
        f.Encoding.ReceivedCalls().ShouldBeEmpty();
        f.Publication.ReceivedCalls().ShouldBeEmpty();
        graph.Verifications.ShouldBeEmpty();
    }

    [Fact]
    public async Task Execute_NewerDemandDuringSettlement_ReplansAndAcknowledgesOnlyTheCurrentVersion()
    {
        var f = new Fixture(MediaReconciliationOperation.Verify);
        long version = 1;
        f.Planning.NextAsync(f.Job.Id, Arg.Any<MediaReconciliationDemandState>(), Arg.Any<CancellationToken>()).Returns((ErrorOr<MediaReconciliationDirective>)new MediaReconciliationDirective.Settle(MediaReconciliationOutcomeKind.Completed, "no-work"));
        f.Execution.ReadDemandAsync(f.Job.Id, Arg.Any<CancellationToken>()).Returns(_ => new MediaReconciliationDemandState(f.Subject, version, false, false));
        f.Execution.FinishAsync(f.Job.Id, Arg.Any<long>(), MediaReconciliationOutcomeKind.Completed, "no-work", true, Arg.Any<CancellationToken>()).Returns(call =>
        {
            if (call.Arg<long>() == 1)
            { version = 2; return MediaReconciliationErrors.Changed; }
            return f.Outcome(MediaReconciliationOutcomeKind.Completed, version, "no-work");
        });
        await f.Executor.ExecuteAsync(f.Job, f.Context);
        f.Job.Outcome.ShouldBe(MediaReconciliationOutcomeKind.Completed);
        version.ShouldBe(2);
        await f.Execution.Received(2).SupersedeAttemptAsync(f.Job.Id, "no-further-work", true, Arg.Any<CancellationToken>());
        f.Media.ReceivedCalls().ShouldBeEmpty();
    }

    [Fact]
    public async Task Execute_RepeatedChangedCapture_IsBoundedWithoutNativeAdmissionOrAcknowledgement()
    {
        var f = new Fixture(MediaReconciliationOperation.Verify);
        f.Planning.NextAsync(f.Job.Id, Arg.Any<MediaReconciliationDemandState>(), Arg.Any<CancellationToken>()).Returns((ErrorOr<MediaReconciliationDirective>)f.Directive);
        f.Execution.CaptureAsync(f.Capture, Arg.Any<CancellationToken>()).Returns(MediaReconciliationErrors.Changed);
        await f.Executor.ExecuteAsync(f.Job, f.Context);
        await f.Execution.Received(16).CaptureAsync(f.Capture, Arg.Any<CancellationToken>());
        f.Media.ReceivedCalls().ShouldBeEmpty();
        f.Context.DeferredUntil.ShouldNotBeNull();
        f.Job.IsTerminal.ShouldBeFalse();
    }

    [Fact]
    public async Task Execute_Encoding_KeepsPayloadOwnedThroughPublicationAndUsesTheVerificationCheckpoint()
    {
        var f = new Fixture(MediaReconciliationOperation.Encode);
        bool released = false, disposed = false;
        var artifact = Substitute.For<ITempFile>();
        artifact.FileSha256.Returns(f.Graph.ArtifactSha256);
        artifact.FileSize.Returns(f.Graph.ArtifactSize);
        var encoded = new MediaReconciliationEncoded.Whole(artifact, f.Measurement, f.Measurement, f.Capture.Identity.Recipe!, f.Capture.EncoderBuild!, null, DateTimeOffset.UtcNow);
        var lease = Substitute.For<IMediaReconciliationEncodingLease>();
        lease.ProcessTreeStopped.Returns(true);
        f.Encoding.AcquireAsync(Arg.Any<MediaReconciliationAttempt>(), Arg.Any<CancellationToken>()).Returns(ErrorOrFactory.From(lease));
        lease.EncodeAndVerifyAsync(Arg.Any<Func<CancellationToken, Task>>(), Arg.Any<CancellationToken>()).Returns(async call =>
        {
            await call.Arg<Func<CancellationToken, Task>>()(CancellationToken.None);
            f.Job.PhaseEnum.ShouldBe(MediaReconciliationPhase.Verifying);
            return (ErrorOr<MediaReconciliationEncoded>)encoded;
        });
        lease.ReleaseProcessingAsync(Arg.Any<CancellationToken>()).Returns(_ => { released = true; return Result.Success; });
        lease.DisposeAsync().Returns(_ => { disposed = true; return ValueTask.CompletedTask; });
        f.Publication.PublishAsync(Arg.Any<MediaReconciliationAttempt>(), encoded, true, Arg.Any<CancellationToken>()).Returns(_ =>
        {
            released.ShouldBeTrue();
            disposed.ShouldBeFalse();
            f.Job.PhaseEnum.ShouldBe(MediaReconciliationPhase.Publishing);
            return Result.Success;
        });
        await f.Executor.ExecuteAsync(f.Job, f.Context);
        disposed.ShouldBeTrue();
        f.Job.Outcome.ShouldBe(MediaReconciliationOutcomeKind.Completed);
        await f.Publication.Received(1).PublishAsync(Arg.Any<MediaReconciliationAttempt>(), encoded, true, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Execute_CanonicalRelease_PassesTheCapturedCanonicalProofWithoutASecondEncode()
    {
        var f = new Fixture(MediaReconciliationOperation.Release);
        await f.Executor.ExecuteAsync(f.Job, f.Context);
        f.Job.Outcome.ShouldBe(MediaReconciliationOutcomeKind.Completed);
        await f.Publication.Received(1).ReleaseAsync(Arg.Any<MediaReconciliationAttempt>(), f.Graph.Id, f.Graph.Verifications.Single().Id, true, Arg.Any<CancellationToken>());
        f.Encoding.ReceivedCalls().ShouldBeEmpty();
        f.Media.ReceivedCalls().ShouldBeEmpty();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Execute_CancelledMeasurement_RequiresStoppedCustodyBeforeAcknowledgingCancellation(bool stopped)
    {
        var f = new Fixture(MediaReconciliationOperation.Verify);
        f.Execution.IsCancellationRequestedAsync(f.Job.Id, Arg.Any<CancellationToken>()).Returns(true);
        f.VerifyLease.ProcessTreeStopped.Returns(stopped);
        f.VerifyLease.VerifyAsync(Arg.Any<CancellationToken>()).Returns(_ => Task.FromException<ErrorOr<RomMediaVerificationMeasurement>>(new OperationCanceledException()));
        await f.Executor.ExecuteAsync(f.Job, f.Context);
        f.Job.IsTerminal.ShouldBe(stopped);
        if (stopped)
            f.Job.Outcome.ShouldBe(MediaReconciliationOutcomeKind.Cancelled);
        else
            f.Context.DeferredUntil.ShouldNotBeNull();
        await f.Execution.DidNotReceiveWithAnyArgs().RecordVerificationAsync(default!, default!, default);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Execute_WaitingSelection_ReportsOnlyNewDurablyRefreshedChildProgress(bool newProgress)
    {
        var f = new Fixture(MediaReconciliationOperation.Verify);
        var job = MediaReconciliationJob.Create(Guid.NewGuid(), new MediaReconciliationRequest.SelectionWork(new MediaReconciliationFilter.RetainedRepresentations(), new(0, 0, 2, 0), 2, false),
            "selection", null, null, DateTimeOffset.UnixEpoch);
        var saved = new MediaReconciliationSelectionProgress(new MediaReconciliationSubject.Representation(2), 2, 2, 0, 0, newProgress ? 0 : 1, 0, true);
        job.ApplySelectionProgress(saved);
        var current = new MediaReconciliationSelectionState(saved with { Completed = 1 }, false);
        f.Selections.RefreshAsync(job.Id, Arg.Any<CancellationToken>()).Returns(current);
        f.Selections.FinishAsync(job.Id, MediaReconciliationOutcomeKind.Completed, Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(MediaReconciliationErrors.Changed);
        int progress = 0;
        bool? deferredProgress = null;
        var context = new JobContext(null, _ => Task.CompletedTask, CancellationToken.None, jobId: job.Id,
            onDeferral: (_, madeProgress) => deferredProgress = madeProgress, onUploadProgress: () => progress++);
        await f.Executor.ExecuteAsync(job, context);
        job.SelectionProgress.ShouldBe(current.Progress);
        job.IsTerminal.ShouldBeFalse();
        context.DeferredUntil.ShouldNotBeNull();
        progress.ShouldBe(newProgress ? 1 : 0);
        deferredProgress.ShouldBe(newProgress);
        await f.Selections.Received(1).RefreshAsync(job.Id, Arg.Any<CancellationToken>());
        await f.Selections.DidNotReceiveWithAnyArgs().AdvanceAsync(default, default, default);
        f.Media.ReceivedCalls().ShouldBeEmpty();
        f.Encoding.ReceivedCalls().ShouldBeEmpty();
    }

    [Fact]
    public async Task Execute_SelectionTerminalRecovery_AppliesCommittedCountsBeforeItsOutcome()
    {
        var f = new Fixture(MediaReconciliationOperation.Verify);
        var job = MediaReconciliationJob.Create(Guid.NewGuid(), new MediaReconciliationRequest.SelectionWork(new MediaReconciliationFilter.RetainedRepresentations(), new(0, 0, 1, 0), 1, false),
            "selection", null, null, DateTimeOffset.UnixEpoch);
        f.Selections.ReadAsync(job.Id, Arg.Any<CancellationToken>()).Returns(new MediaReconciliationSelectionState(new(new MediaReconciliationSubject.Representation(1), 1, 1, 0, 0, 1, 1, true), false));
        f.Execution.ReadOutcomeAsync(job.Id, Arg.Any<CancellationToken>()).Returns(new MediaReconciliationJournalOutcome(job.Id, MediaReconciliationOutcomeKind.CompletedWithErrors, "selected-work-complete", 0, DateTimeOffset.UtcNow));
        await f.Executor.ExecuteAsync(job, f.Context);
        job.Outcome.ShouldBe(MediaReconciliationOutcomeKind.CompletedWithErrors);
        job.SelectionProgress!.Failed.ShouldBe(1);
        f.Planning.ReceivedCalls().ShouldBeEmpty();
        f.Media.ReceivedCalls().ShouldBeEmpty();
    }

    [Theory]
    [InlineData(JobTerminationCause.Cancelled)]
    [InlineData(JobTerminationCause.PermanentFailure)]
    public async Task Lifecycle_Termination_UsesTheCurrentDemandAndDurableTerminalJournal(JobTerminationCause cause)
    {
        var f = new Fixture(MediaReconciliationOperation.Verify);
        var lifecycle = new MediaReconciliationLifecyclePolicy(f.Execution, f.Selections);
        var result = await lifecycle.ReconcileAsync(f.Job, cause);
        result.Kind.ShouldBe(JobTerminationKind.Settled);
        f.Job.Outcome.ShouldBe(cause == JobTerminationCause.Cancelled ? MediaReconciliationOutcomeKind.Cancelled : MediaReconciliationOutcomeKind.Failed);
        await f.Execution.Received(1).FinishAsync(f.Job.Id, 1, Arg.Any<MediaReconciliationOutcomeKind>(), Arg.Any<string>(), true, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Lifecycle_UnknownCustody_DefersWithoutApplyingATerminalSnapshot()
    {
        var f = new Fixture(MediaReconciliationOperation.Verify);
        f.Execution.FinishAsync(f.Job.Id, 1, Arg.Any<MediaReconciliationOutcomeKind>(), Arg.Any<string>(), true, Arg.Any<CancellationToken>()).Returns(MediaReconciliationErrors.Custody);
        var result = await new MediaReconciliationLifecyclePolicy(f.Execution, f.Selections).ReconcileAsync(f.Job, JobTerminationCause.PermanentFailure);
        result.Kind.ShouldBe(JobTerminationKind.Deferred);
        f.Job.IsTerminal.ShouldBeFalse();
    }

    [Fact]
    public async Task Lifecycle_AmbiguousTerminalCommit_RecoversTheIndependentJournal()
    {
        var f = new Fixture(MediaReconciliationOperation.Verify);
        bool committed = false;
        f.Execution.ReadOutcomeAsync(f.Job.Id, Arg.Any<CancellationToken>()).Returns(_ => committed ? f.Outcome(MediaReconciliationOutcomeKind.Completed) : null);
        f.Execution.FinishAsync(f.Job.Id, 1, Arg.Any<MediaReconciliationOutcomeKind>(), Arg.Any<string>(), true, Arg.Any<CancellationToken>()).Returns(_ =>
        {
            committed = true;
            return Task.FromException<ErrorOr<MediaReconciliationJournalOutcome>>(new IOException("terminal commit response lost"));
        });
        var result = await new MediaReconciliationLifecyclePolicy(f.Execution, f.Selections).ReconcileAsync(f.Job, JobTerminationCause.Cancelled);
        result.Kind.ShouldBe(JobTerminationKind.Settled);
        f.Job.Outcome.ShouldBe(MediaReconciliationOutcomeKind.Completed);
    }

    private static Sha256 Hash(char value) => Sha256.Parse(new string(value, 64));
    private sealed class Fixture
    {
        public IMediaReconciliationExecution Execution { get; } = Substitute.For<IMediaReconciliationExecution>();
        public IMediaReconciliationPlanning Planning { get; } = Substitute.For<IMediaReconciliationPlanning>();
        public IMediaReconciliationSelections Selections { get; } = Substitute.For<IMediaReconciliationSelections>();
        public IRomOriginalMediaInspection Originals { get; } = Substitute.For<IRomOriginalMediaInspection>();
        public IRomOriginalMediaInspectionLease OriginalLease { get; } = Substitute.For<IRomOriginalMediaInspectionLease>();
        public IRomMediaVerification Media { get; } = Substitute.For<IRomMediaVerification>();
        public IRomMediaVerificationLease VerifyLease { get; } = Substitute.For<IRomMediaVerificationLease>();
        public IMediaReconciliationEncoding Encoding { get; } = Substitute.For<IMediaReconciliationEncoding>();
        public IMediaReconciliationPublication Publication { get; } = Substitute.For<IMediaReconciliationPublication>();
        public MediaReconciliationSubject Subject { get; }
        public MediaReconciliationJob Job { get; }
        public MediaReconciliationCapture Capture { get; }
        public MediaReconciliationDirective Directive { get; }
        public RomRepresentationFacts Graph { get; }
        public RomMediaVerificationMeasurement.Whole Measurement { get; }
        public MediaReconciliationJobExecutor Executor { get; }
        public JobContext Context { get; } = new(null, _ => Task.CompletedTask, CancellationToken.None);
        public Fixture(MediaReconciliationOperation operation, bool original = false)
        {
            var logical = MediaFactFixtures.Identity(new byte[256]);
            RomContainerFacts container = original ? RomContainerFacts.Identity.Instance : MediaFactFixtures.Rvz(logical.Size, 1, 5, 19, 131072, false);
            Sha256 hash = original ? logical.Sha256 : Hash('b');
            long bytes = original ? logical.Size : 64;
            var prior = MediaFactFixtures.WholeMeasurement(Guid.NewGuid(), hash, bytes, logical, container, new RomDecoderIdentity.Managed("prior-decoder"), "fixture-profile", "fixture-verifier", DateTimeOffset.UnixEpoch, null, null);
            var current = MediaFactFixtures.WholeMeasurement(Guid.NewGuid(), hash, bytes, logical, container, new RomDecoderIdentity.Managed("resident-decoder"), "fixture-profile", "fixture-verifier", DateTimeOffset.UnixEpoch.AddSeconds(1), null, null);
            Graph = new(3, 2, hash, bytes, container, RomReconstructionManifest.WholeFile.Instance, [new(4, 7, RomOutputSelector.Whole.Instance, logical)], [prior], []);
            Measurement = new(new(container, Graph.Manifest, current), new(Guid.NewGuid(), logical, "fixture-profile", "fixture-verifier", current.ObservedAt, new byte[32]),
                new(hash, Md5.Parse(new string('c', 32)), Sha1.Parse(new string('d', 40)), bytes));
            Subject = operation == MediaReconciliationOperation.Verify ? new MediaReconciliationSubject.Representation(Graph.Id) : new MediaReconciliationSubject.LogicalRom(7);
            Job = MediaReconciliationJob.Create(Guid.NewGuid(), new MediaReconciliationRequest.SubjectWork(Subject), "fixture", null, null, DateTimeOffset.UnixEpoch);
            Job.Start("fixture", startedAt: DateTimeOffset.UtcNow);
            var input = new MediaReconciliationInput.Logical(MediaReconciliationSourceRole.Whole, "disc.iso", Graph, 4, prior.Id);
            bool policy = operation is MediaReconciliationOperation.Encode or MediaReconciliationOperation.Release;
            Capture = new(Job.Id, 1, new(operation == MediaReconciliationOperation.Inspect || policy ? [input] : [new MediaReconciliationInput.Artifact(Graph, null, null)], null, [], null), operation,
                policy ? RecipeId.Parse("fixture-recipe/v1") : null, Hash('c'), Hash('d'), current.Decoder, policy ? "gamecube-disc" : null,
                operation == MediaReconciliationOperation.Encode ? "actual-encoder" : null, operation == MediaReconciliationOperation.Encode ? RomIsoDiscKind.GameCube : null);
            Directive = operation switch
            {
                MediaReconciliationOperation.Inspect => new MediaReconciliationDirective.Inspect(Capture, input),
                MediaReconciliationOperation.Verify => new MediaReconciliationDirective.Verify(Capture, new(Graph.Id, Graph.FileId, hash, bytes, current.Decoder)),
                MediaReconciliationOperation.Encode => new MediaReconciliationDirective.Encode(Capture),
                _ => new MediaReconciliationDirective.Release(Capture, Graph.Id, prior.Id)
            };
            Execution.ReadDemandAsync(Job.Id, Arg.Any<CancellationToken>()).Returns(new MediaReconciliationDemandState(Subject, 1, false, false));
            Execution.CaptureAsync(Capture, Arg.Any<CancellationToken>()).Returns(new MediaReconciliationAttempt(1, Capture, null));
            Execution.SupersedeAttemptAsync(Job.Id, Arg.Any<string>(), true, Arg.Any<CancellationToken>()).Returns(Result.Success);
            Execution.RecordVerificationAsync(Arg.Any<MediaReconciliationAttempt>(), Arg.Any<RomMediaVerificationMeasurement>(), true, Arg.Any<CancellationToken>()).Returns(Result.Success);
            Execution.FailAttemptAsync(Arg.Any<MediaReconciliationAttempt>(), Arg.Any<string>(), true, true, Arg.Any<CancellationToken>()).Returns(Result.Success);
            Execution.FinishAsync(Job.Id, Arg.Any<long>(), Arg.Any<MediaReconciliationOutcomeKind>(), Arg.Any<string>(), true, Arg.Any<CancellationToken>())
                .Returns(call => Outcome(call.Arg<MediaReconciliationOutcomeKind>(), call.Arg<long>(), call.Arg<string>()));
            Planning.NextAsync(Job.Id, Arg.Any<MediaReconciliationDemandState>(), Arg.Any<CancellationToken>()).Returns((ErrorOr<MediaReconciliationDirective>)Directive,
                new MediaReconciliationDirective.Settle(MediaReconciliationOutcomeKind.Completed, "complete"));
            Media.AcquireAsync(Job.Id, Arg.Any<RomMediaVerificationSource>(), Arg.Any<CancellationToken>()).Returns(ErrorOrFactory.From(VerifyLease));
            VerifyLease.ProcessTreeStopped.Returns(true);
            VerifyLease.VerifyAsync(Arg.Any<CancellationToken>()).Returns((RomMediaVerificationMeasurement)Measurement);
            VerifyLease.ReleaseProcessingAsync(Arg.Any<CancellationToken>()).Returns(Result.Success);
            Originals.AcquireAsync(Job.Id, input, (RomDecoderIdentity.Managed)current.Decoder, Arg.Any<CancellationToken>()).Returns(ErrorOrFactory.From(OriginalLease));
            OriginalLease.ProcessTreeStopped.Returns(true);
            OriginalLease.InspectAsync(Arg.Any<CancellationToken>()).Returns(Measurement);
            OriginalLease.ReleaseProcessingAsync(Arg.Any<CancellationToken>()).Returns(Result.Success);
            Publication.ReleaseAsync(Arg.Any<MediaReconciliationAttempt>(), Graph.Id, prior.Id, true, Arg.Any<CancellationToken>()).Returns(Result.Success);
            Executor = new(Execution, Planning, Selections, Originals, Media, Encoding, Substitute.For<IMediaReconciliationAdoption>(), Publication, TimeProvider.System);
        }
        public MediaReconciliationJournalOutcome Outcome(MediaReconciliationOutcomeKind kind, long version = 1, string code = "complete") => new(Job.Id, kind, code, version, DateTimeOffset.UtcNow);
    }
}
