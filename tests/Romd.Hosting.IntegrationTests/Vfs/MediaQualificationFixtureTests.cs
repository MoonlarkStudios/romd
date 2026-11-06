using Romd.Application.Common.Content;
using Romd.Domain.Source.Rom;
using Romd.PostgreSql.TestSupport;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Romd.Consumer.Application.Delivery;
using Romd.Domain.Hashing;
using Romd.Infrastructure.Delivery;
using Shouldly;
using Xunit;

namespace Romd.Hosting.IntegrationTests.Vfs;

public sealed class MediaQualificationFixtureTests
{
    [Fact]
    public void CreateDeliveryOptions_EphemeralFixtureConfiguration_RoundTripsAnOriginalGrant()
    {
        var options = MediaQualificationFixture.CreateDeliveryOptions();
        var service = new ConsumerContentGrantService(Options.Create(options), TimeProvider.System);
        var identity = MediaFactFixtures.Identity("123456789"u8);
        var target = new RomDeliveryTarget(4, 5, RomDeliveryFormat.Original, "qualification.txt", identity.Sha256, identity.Size,
            identity.Sha256, identity.Size, identity.Sha256, identity.Size, RomRepresentationEncoding.Identity, identity.Md5, identity.Sha1);
        var grant = new ConsumerContentGrant(Guid.NewGuid(), 1, 2, 3, target);

        var issued = service.IssueDownloadGrant(grant);
        var validated = service.ValidateDownloadGrant(issued.DownloadUrl[(issued.DownloadUrl.LastIndexOf('/') + 1)..]);

        validated.IsError.ShouldBeFalse();
        validated.Value.Grant.ShouldBe(grant);
        validated.Value.ExpiresAt.Subtract(validated.Value.IssuedAt).ShouldBe(TimeSpan.FromMinutes(2));
    }

    [Fact]
    public void CreateDeliveryOptions_IndependentFixtures_RejectsOtherFixtureGrant()
    {
        var firstOptions = MediaQualificationFixture.CreateDeliveryOptions();
        var secondOptions = MediaQualificationFixture.CreateDeliveryOptions();
        secondOptions.SigningKeyId = firstOptions.SigningKeyId;
        var issuer = new ConsumerContentGrantService(Options.Create(firstOptions), TimeProvider.System);
        var validator = new ConsumerContentGrantService(Options.Create(secondOptions), TimeProvider.System);
        var identity = MediaFactFixtures.Identity("123456789"u8);
        var target = new RomDeliveryTarget(4, 5, RomDeliveryFormat.Original, "qualification.txt", identity.Sha256, identity.Size,
            identity.Sha256, identity.Size, identity.Sha256, identity.Size, RomRepresentationEncoding.Identity, identity.Md5, identity.Sha1);
        var grant = new ConsumerContentGrant(Guid.NewGuid(), 1, 2, 3, target);
        var issued = issuer.IssueDownloadGrant(grant);

        var validated = validator.ValidateDownloadGrant(issued.DownloadUrl[(issued.DownloadUrl.LastIndexOf('/') + 1)..]);

        validated.IsError.ShouldBeTrue();
    }

    [Fact]
    public async Task LoadAsync_TwoDistinctDiscKinds_AcceptsReadOnlyFilesWithoutCreatingReport()
    {
        using var sandbox = new Sandbox();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var inputs = await sandbox.InputsAsync(deadline.Token);
        await sandbox.WriteConfigAsync(inputs, deadline.Token);
        var loaded = await MediaQualificationFixture.LoadAsync(sandbox.Config, sandbox.Report, deadline.Token);
        loaded.Count.ShouldBe(2);
        loaded.Select(input => input.SystemKey).ShouldBe(["gc", "wii"]);
        File.Exists(sandbox.Report).ShouldBeFalse();
        (await File.ReadAllBytesAsync(inputs[0].Iso, deadline.Token)).ShouldBe(new byte[] { 1, 2, 3 });
    }

    [Fact]
    public async Task LoadAsync_ExistingReport_RefusesOverwriteAndPreservesInputs()
    {
        using var sandbox = new Sandbox();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var inputs = await sandbox.InputsAsync(deadline.Token);
        await sandbox.WriteConfigAsync(inputs, deadline.Token);
        byte[] witness = [9, 8, 7];
        await File.WriteAllBytesAsync(sandbox.Report, witness, deadline.Token);
        await Should.ThrowAsync<InvalidDataException>(() =>
            MediaQualificationFixture.LoadAsync(sandbox.Config, sandbox.Report, deadline.Token));
        (await File.ReadAllBytesAsync(sandbox.Report, deadline.Token)).ShouldBe(witness);
        (await File.ReadAllBytesAsync(inputs[0].Iso, deadline.Token)).ShouldBe(new byte[] { 1, 2, 3 });
    }

    [Theory]
    [InlineData("duplicate-kind")]
    [InlineData("third-disc")]
    [InlineData("unsupported-kind")]
    [InlineData("relative-input")]
    [InlineData("missing-input")]
    [InlineData("empty-dat-id")]
    public async Task LoadAsync_InvalidSelection_RejectsBeforeReportSideEffects(string mutation)
    {
        using var sandbox = new Sandbox();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var inputs = (await sandbox.InputsAsync(deadline.Token)).ToList();
        switch (mutation)
        {
            case "duplicate-kind":
                inputs[1] = inputs[1] with { NintendoKind = "GameCube" };
                break;
            case "third-disc":
                inputs.Add(inputs[0] with { Name = "Third" });
                break;
            case "unsupported-kind":
                inputs[0] = inputs[0] with { NintendoKind = "Other" };
                break;
            case "relative-input":
                inputs[0] = inputs[0] with { Iso = "disc.iso" };
                break;
            case "missing-input":
                inputs[0] = inputs[0] with { Iso = Path.Combine(sandbox.Root, "missing.iso") };
                break;
            case "empty-dat-id":
                inputs[0] = inputs[0] with { DatId = "" };
                break;
        }
        await sandbox.WriteConfigAsync(inputs, deadline.Token);
        await Should.ThrowAsync<InvalidDataException>(() =>
            MediaQualificationFixture.LoadAsync(sandbox.Config, sandbox.Report, deadline.Token));
        File.Exists(sandbox.Report).ShouldBeFalse();
    }

    [Fact]
    public async Task LoadAsync_ProspectiveReportUnderFixture_RejectsAndPreservesFixture()
    {
        using var sandbox = new Sandbox();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var inputs = await sandbox.InputsAsync(deadline.Token);
        await sandbox.WriteConfigAsync(inputs, deadline.Token);
        await Should.ThrowAsync<InvalidDataException>(() => MediaQualificationFixture.LoadAsync(
            sandbox.Config, Path.Combine(inputs[0].Iso, "new-report.json"), deadline.Token));
        (await File.ReadAllBytesAsync(inputs[0].Iso, deadline.Token)).ShouldBe(new byte[] { 1, 2, 3 });
    }

    [Fact]
    public async Task LoadAsync_DuplicateConfigurationField_RejectsAmbiguousIdentity()
    {
        using var sandbox = new Sandbox();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await File.WriteAllTextAsync(sandbox.Config, "[{\"Name\":\"first\",\"Name\":\"second\"}]", deadline.Token);
        await Should.ThrowAsync<InvalidDataException>(() =>
            MediaQualificationFixture.LoadAsync(sandbox.Config, sandbox.Report, deadline.Token));
        File.Exists(sandbox.Report).ShouldBeFalse();
    }

    [Fact]
    public async Task LoadAsync_OversizedConfiguration_RejectsBeforeParsingAndPreservesReportAbsence()
    {
        using var sandbox = new Sandbox();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await File.WriteAllBytesAsync(sandbox.Config,
            new byte[MediaQualificationFixture.MaximumConfigurationBytes + 1], deadline.Token);
        await Should.ThrowAsync<InvalidDataException>(() =>
            MediaQualificationFixture.LoadAsync(sandbox.Config, sandbox.Report, deadline.Token));
        File.Exists(sandbox.Report).ShouldBeFalse();
    }

    [Fact]
    public async Task MeasureAsync_ShortNonSeekableReads_MatchesIndependentChecksumVectorAndEof()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var input = new ShortReadStream(Encoding.ASCII.GetBytes("123456789"));
        var facts = await MediaQualificationFixture.MeasureAsync(input, 9, deadline.Token);
        facts.ShouldBe(new MediaQualificationFacts(9,
            "15e2b0d3c33891ebb0f1ef609ec419420c20e320ce94c65fbc8c3312448eb225",
            "25f9e794323b453885f5181f1b624d0b", "f7c3bc1d808e04732adf679965ccc34ca7ae3441", "cbf43926"));
        input.EofRead.ShouldBeTrue();
    }

    [Fact]
    public async Task MeasureAsync_AdvertisedLengthTooSmall_RejectsUnexpectedTrailingBytes()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var input = new ShortReadStream([1, 2, 3]);
        await Should.ThrowAsync<InvalidDataException>(() =>
            MediaQualificationFixture.MeasureAsync(input, 2, deadline.Token));
    }

    [Fact]
    public async Task MeasureAsync_CanceledToken_DoesNotReadOrTreatPartialStreamAsVerified()
    {
        using var canceled = new CancellationTokenSource();
        await canceled.CancelAsync();
        await using var input = new ShortReadStream([1, 2, 3]);
        await Should.ThrowAsync<OperationCanceledException>(() =>
            MediaQualificationFixture.MeasureAsync(input, 3, canceled.Token));
        input.Reads.ShouldBe(0);
    }

    [Theory]
    [InlineData("valid")]
    [InlineData("extra")]
    [InlineData("missing")]
    [InlineData("renamed")]
    [InlineData("wrong-bytes")]
    [InlineData("traversal")]
    public async Task ValidateZipAsync_MixedMembers_RequiresExactSafeNamesAndHashes(string scenario)
    {
        using var sandbox = new Sandbox();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var ct = deadline.Token;
        byte[] disc = [1, 2, 3, 4];
        byte[] notes = Encoding.UTF8.GetBytes("notes");
        await using var discSource = new MemoryStream(disc, writable: false);
        await using var notesSource = new MemoryStream(notes, writable: false);
        var expected = new Dictionary<string, MediaQualificationFacts>(StringComparer.Ordinal)
        {
            ["Disc/game.rvz"] = await MediaQualificationFixture.MeasureAsync(discSource, disc.Length, ct),
            ["notes.txt"] = await MediaQualificationFixture.MeasureAsync(notesSource, notes.Length, ct)
        };
        string path = Path.Combine(sandbox.Root, "archive.zip");
        await using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        await using (var archive = await ZipArchive.CreateAsync(output, ZipArchiveMode.Create, leaveOpen: true,
            entryNameEncoding: null, cancellationToken: ct))
        {
            async Task AddAsync(string name, byte[] bytes, CancellationToken token)
            {
                await using var entry = await archive.CreateEntry(name, CompressionLevel.NoCompression).OpenAsync(token);
                await entry.WriteAsync(bytes, token);
            }
            await AddAsync(scenario == "renamed" ? "Disc/other.rvz" : scenario == "traversal" ? "../game.rvz" : "Disc/game.rvz",
                scenario == "wrong-bytes" ? new byte[] { 4, 3, 2, 1 } : disc, ct);
            if (scenario != "missing")
                await AddAsync("notes.txt", notes, ct);
            if (scenario == "extra")
                await AddAsync("extra.txt", notes, ct);
        }
        if (scenario == "valid")
            await MediaQualificationFixture.ValidateZipAsync(path, expected, ct);
        else
            await Should.ThrowAsync<InvalidDataException>(() =>
            MediaQualificationFixture.ValidateZipAsync(path, expected, ct));
        File.Exists(path).ShouldBeTrue();
    }

    [Theory]
    [InlineData("valid")]
    [InlineData("wrong-hash")]
    [InlineData("wrong-id")]
    [InlineData("duplicate")]
    public async Task ReadDatAsync_StreamingRequirement_UsesCompleteMatchingIdentity(string scenario)
    {
        using var sandbox = new Sandbox();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var ct = deadline.Token;
        await using var source = new MemoryStream(Encoding.ASCII.GetBytes("123456789"), writable: false);
        var facts = await MediaQualificationFixture.MeasureAsync(source, 9, ct);
        string game = "<game id=\"1\" name=\"Disc\"><rom name=\"Disc.iso\" size=\"9\" crc=\"cbf43926\" " +
            "md5=\"25f9e794323b453885f5181f1b624d0b\" sha1=\"" +
            (scenario == "wrong-hash" ? new string('0', 40) : facts.Sha1) + "\"/></game>";
        string dat = Path.Combine(sandbox.Root, "disc.dat");
        await File.WriteAllTextAsync(dat, "<datafile>" + game + (scenario == "duplicate" ? game : "") + "</datafile>", ct);
        var input = new MediaQualificationInput("Disc", Path.Combine(sandbox.Root, "disc.iso"),
            Path.Combine(sandbox.Root, "disc.rvz"), dat, scenario == "wrong-id" ? "2" : "1", "GameCube");
        if (scenario == "valid")
            (await MediaQualificationFixture.ReadDatAsync(input, facts, ct)).RomName.ShouldBe("Disc.iso");
        else
            await Should.ThrowAsync<InvalidDataException>(() => MediaQualificationFixture.ReadDatAsync(input, facts, ct));
    }

    [Fact]
    public async Task CancelAndDisposeResponseAsync_AlreadyGrantedResponse_DisposesOwnedBody()
    {
        using var stop = new CancellationTokenSource();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var body = new TrackingContent();
        var response = new HttpResponseMessage(System.Net.HttpStatusCode.PartialContent) { Content = body };
        await MediaQualificationFixture.CancelAndDisposeResponseAsync(Task.FromResult(response), stop, deadline.Token);
        stop.IsCancellationRequested.ShouldBeTrue();
        body.Disposed.ShouldBeTrue();
    }

    [Fact]
    public async Task CancelAndDisposeResponseAsync_QueuedCancellation_JoinsCancelledRequest()
    {
        using var stop = new CancellationTokenSource();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var pending = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = stop.Token.Register(() => pending.TrySetCanceled(stop.Token));
        await MediaQualificationFixture.CancelAndDisposeResponseAsync(pending.Task, stop, deadline.Token);
        pending.Task.IsCanceled.ShouldBeTrue();
    }

    [Fact]
    public async Task CancelAndDisposeResponseAsync_GrantRacesCancellation_JoinsAndDisposesResponse()
    {
        using var stop = new CancellationTokenSource();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var body = new TrackingContent();
        var response = new HttpResponseMessage(System.Net.HttpStatusCode.PartialContent) { Content = body };
        var pending = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = stop.Token.Register(() => pending.TrySetResult(response));
        await MediaQualificationFixture.CancelAndDisposeResponseAsync(pending.Task, stop, deadline.Token);
        pending.Task.IsCompletedSuccessfully.ShouldBeTrue();
        body.Disposed.ShouldBeTrue();
    }

    [Fact]
    public async Task CancelAndDisposeResponseAsync_RequestFault_DoesNotMaskUnexpectedFailureAsCancellation()
    {
        using var stop = new CancellationTokenSource();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var fault = new HttpRequestException("test transport failed");
        var pending = Task.FromException<HttpResponseMessage>(fault);
        var actual = await Should.ThrowAsync<HttpRequestException>(() =>
            MediaQualificationFixture.CancelAndDisposeResponseAsync(pending, stop, deadline.Token));
        actual.ShouldBeSameAs(fault);
    }

    [Fact]
    public async Task CancelAndDisposeResponseAsync_CallbackThrowsAfterGrant_DisposesResponseBeforeSurfacingFailure()
    {
        using var stop = new CancellationTokenSource();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var body = new TrackingContent();
        using var response = new HttpResponseMessage(System.Net.HttpStatusCode.PartialContent) { Content = body };
        var callbackFailure = new InvalidOperationException("test cancellation callback failed");
        var pending = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = stop.Token.Register(() =>
        {
            pending.TrySetResult(response);
            throw callbackFailure;
        });
        var actual = await Should.ThrowAsync<AggregateException>(() =>
            MediaQualificationFixture.CancelAndDisposeResponseAsync(pending.Task, stop, deadline.Token));
        actual.Flatten().InnerExceptions.ShouldContain(callbackFailure);
        pending.Task.IsCompletedSuccessfully.ShouldBeTrue();
        body.Disposed.ShouldBeTrue();
    }

    [Fact]
    public async Task CancelAndDisposeResponseAsync_CallbackThrowsBeforeDelayedGrant_JoinsThenDisposesBeforeFailure()
    {
        using var stop = new CancellationTokenSource();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var body = new TrackingContent();
        using var response = new HttpResponseMessage(System.Net.HttpStatusCode.PartialContent) { Content = body };
        var callbackFailure = new InvalidOperationException("test delayed cancellation callback failed");
        var callbackEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = stop.Token.Register(() =>
        {
            callbackEntered.TrySetResult(true);
            throw callbackFailure;
        });
        var cleanup = MediaQualificationFixture.CancelAndDisposeResponseAsync(pending.Task, stop, deadline.Token);
        try
        {
            await callbackEntered.Task.WaitAsync(deadline.Token);
            Task guard = Task.Delay(100, deadline.Token);
            var completed = await Task.WhenAny(cleanup, guard);
            completed.ShouldBeSameAs(guard, "Cleanup must keep owning the request until its delayed grant is joined.");
            pending.TrySetResult(response);
            var actual = await Should.ThrowAsync<AggregateException>(() => cleanup);
            actual.Flatten().InnerExceptions.ShouldContain(callbackFailure);
            body.Disposed.ShouldBeTrue();
        }
        finally
        {
            pending.TrySetResult(response);
            try
            { await cleanup; }
            catch (AggregateException error) when (error.Flatten().InnerExceptions.Contains(callbackFailure)) { }
        }
    }

    [Fact]
    public async Task ExecuteWithRevocationAsync_PrimaryFailureAndDelete500_PreservesExactPrimaryFailure()
    {
        using var handler = new FixedRevocationHandler(System.Net.HttpStatusCode.InternalServerError);
        using var client = new HttpClient(handler) { BaseAddress = new Uri("https://localhost"), Timeout = Timeout.InfiniteTimeSpan };
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var primary = new InvalidOperationException("test primary qualification failed");
        Exception? actual = null;
        try
        {
            await MediaQualificationFixture.ExecuteWithRevocationAsync(_ => Task.FromException(primary),
                client, Guid.NewGuid(), TimeSpan.FromSeconds(1), deadline.Token);
        }
        catch (Exception failure) { actual = failure; }
        actual.ShouldBeSameAs(primary);
        handler.Body.Disposed.ShouldBeTrue();
    }

    [Fact]
    public async Task ExecuteWithRevocationAsync_CleanupDeadline_CancelsAndJoinsBeforeCompleting()
    {
        using var handler = new DelayedRevocationHandler();
        using var client = new HttpClient(handler) { BaseAddress = new Uri("https://localhost"), Timeout = Timeout.InfiniteTimeSpan };
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var cleanup = MediaQualificationFixture.ExecuteWithRevocationAsync(_ => Task.CompletedTask,
            client, Guid.NewGuid(), TimeSpan.FromMilliseconds(40), deadline.Token);
        try
        {
            await handler.Entered.Task.WaitAsync(deadline.Token);
            Task guard = Task.Delay(1000, deadline.Token);
            var completed = await Task.WhenAny(handler.CancellationObserved.Task, guard);
            completed.ShouldBeSameAs(handler.CancellationObserved.Task, "The independent cleanup deadline must cancel the HTTP request.");
            cleanup.IsCompleted.ShouldBeFalse("Cancellation must not complete cleanup before the handler's owned work is joined.");
            handler.AllowCancellationJoin.TrySetResult(true);
            await Should.ThrowAsync<OperationCanceledException>(() => cleanup);
            handler.Finished.Task.IsCompletedSuccessfully.ShouldBeTrue();
        }
        finally
        {
            handler.AllowCancellationJoin.TrySetResult(true);
            handler.ReleaseSuccessfully();
            try
            { await cleanup; }
            catch (OperationCanceledException) when (handler.CancellationObserved.Task.IsCompletedSuccessfully) { }
        }
    }

    [Fact]
    public async Task ExecuteWithRevocationAsync_SuccessfulActionAndDelete500_RemainsAnObservedFailure()
    {
        using var handler = new FixedRevocationHandler(System.Net.HttpStatusCode.InternalServerError);
        using var client = new HttpClient(handler) { BaseAddress = new Uri("https://localhost"), Timeout = Timeout.InfiniteTimeSpan };
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await Should.ThrowAsync<InvalidDataException>(() => MediaQualificationFixture.ExecuteWithRevocationAsync(
            _ => Task.CompletedTask, client, Guid.NewGuid(), TimeSpan.FromSeconds(1), deadline.Token));
        handler.Body.Disposed.ShouldBeTrue();
    }

    [Fact]
    public async Task ExecuteWithRevocationAsync_PrimaryRefusesDiagnosticMutation_PreservesPrimary()
    {
        using var handler = new FixedRevocationHandler(System.Net.HttpStatusCode.InternalServerError);
        using var client = new HttpClient(handler) { BaseAddress = new Uri("https://localhost"), Timeout = Timeout.InfiniteTimeSpan };
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var primary = new ReadOnlyDiagnosticException();
        Exception? actual = null;
        try
        {
            await MediaQualificationFixture.ExecuteWithRevocationAsync(_ => Task.FromException(primary),
                client, Guid.NewGuid(), TimeSpan.FromSeconds(1), deadline.Token);
        }
        catch (Exception failure) { actual = failure; }
        actual.ShouldBeSameAs(primary);
        handler.Body.Disposed.ShouldBeTrue();
    }

    [Fact]
    public void ReleaseContentLeasesAfterJoin_UnjoinedRequestAndReadOnlyPrimary_ReportsLeakAndKeepsHoldsPinned()
    {
        var pending = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var held = new ContentLeaseWitness();
        var primary = new ReadOnlyDiagnosticException();
        try
        {
            var evidence = MediaQualificationFixture.ReleaseContentLeasesAfterJoin([held], pending.Task, primary, null);
            evidence.State.ShouldBe("failed");
            evidence.UnjoinedRequest.ShouldBeTrue();
            evidence.FailureCount.ShouldBe(1);
            held.Releases.ShouldBe(0);
        }
        finally { pending.TrySetResult(true); held.Dispose(); }
    }

    [Fact]
    public void ReleaseContentLeasesAfterJoin_UnjoinedRequestWithoutPrimary_ThrowsAndKeepsHoldsPinned()
    {
        var pending = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var held = new ContentLeaseWitness();
        try
        {
            var failure = Should.Throw<InvalidDataException>(() =>
                MediaQualificationFixture.ReleaseContentLeasesAfterJoin([held], pending.Task, null, null));
            failure.Data["MediaQualification.UnjoinedRequest"].ShouldBe(true);
            held.Releases.ShouldBe(0);
        }
        finally { pending.TrySetResult(true); held.Dispose(); }
    }

    [Fact]
    public void ReleaseContentLeasesAfterJoin_ReleaseThrowsWithPrimary_ReportsFailureAndAttemptsRemainingHolds()
    {
        var failing = new ContentLeaseWitness(throws: true);
        var remaining = new ContentLeaseWitness();
        var primary = new ReadOnlyDiagnosticException();
        var evidence = MediaQualificationFixture.ReleaseContentLeasesAfterJoin([failing, remaining],
            Task.CompletedTask, primary, null);
        evidence.State.ShouldBe("failed");
        evidence.UnjoinedRequest.ShouldBeFalse();
        evidence.FailureCount.ShouldBe(1);
        failing.Releases.ShouldBe(1);
        remaining.Releases.ShouldBe(1);
    }

    private sealed class ReadOnlyDiagnosticException : Exception
    {
        public override System.Collections.IDictionary Data { get; } =
            new System.Collections.ObjectModel.ReadOnlyDictionary<object, object>(new Dictionary<object, object>());
    }

    private sealed class ContentLeaseWitness(bool throws = false) : IDisposable
    {
        public int Releases { get; private set; }
        public void Dispose()
        {
            Releases++;
            if (throws)
                throw new InvalidOperationException("test content lease release failed");
        }
    }

    private sealed class FixedRevocationHandler(System.Net.HttpStatusCode status) : HttpMessageHandler
    {
        public TrackingContent Body { get; } = new();
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            request.Method.ShouldBe(HttpMethod.Delete);
            return Task.FromResult(new HttpResponseMessage(status) { Content = Body });
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing)
                Body.Dispose();
            base.Dispose(disposing);
        }
    }

    private sealed class DelayedRevocationHandler : HttpMessageHandler
    {
        private readonly TaskCompletionSource<HttpResponseMessage> _response = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> CancellationObserved { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> AllowCancellationJoin { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> Finished { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            request.Method.ShouldBe(HttpMethod.Delete);
            Entered.TrySetResult(true);
            using var registration = ct.Register(() => _response.TrySetCanceled(ct));
            try
            { return await _response.Task; }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                CancellationObserved.TrySetResult(true);
                await AllowCancellationJoin.Task;
                throw;
            }
            finally { Finished.TrySetResult(true); }
        }
        public void ReleaseSuccessfully()
        {
            var response = new HttpResponseMessage(System.Net.HttpStatusCode.NoContent);
            if (!_response.TrySetResult(response))
                response.Dispose();
        }
    }

    private sealed class TrackingContent : ByteArrayContent
    {
        public TrackingContent() : base([1, 2, 3]) { }
        public bool Disposed { get; private set; }
        protected override void Dispose(bool disposing)
        {
            if (disposing)
                Disposed = true;
            base.Dispose(disposing);
        }
    }

    private sealed class Sandbox : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "romd-media-helper-" + Guid.NewGuid().ToString("N"));
        public string Config => Path.Combine(Root, "fixtures.json");
        public string Report => Path.Combine(Root, "report.json");
        public Sandbox() => Directory.CreateDirectory(Root);
        public async Task<IReadOnlyList<MediaQualificationInput>> InputsAsync(CancellationToken ct)
        {
            var inputs = new List<MediaQualificationInput>();
            foreach (string kind in new[] { "GameCube", "Wii" })
            {
                string prefix = Path.Combine(Root, kind);
                foreach (string extension in new[] { ".iso", ".rvz", ".dat" })
                    await File.WriteAllBytesAsync(prefix + extension, [1, 2, 3], ct);
                inputs.Add(new(kind, prefix + ".iso", prefix + ".rvz", prefix + ".dat", "1", kind));
            }
            return inputs.AsReadOnly();
        }
        public Task WriteConfigAsync(IEnumerable<MediaQualificationInput> inputs, CancellationToken ct) =>
            File.WriteAllTextAsync(Config, JsonSerializer.Serialize(inputs.Select(input => new
            { input.Name, input.Iso, input.NormalizedRvz, input.Dat, input.DatId, input.NintendoKind })), ct);
        public void Dispose() => Directory.Delete(Root, recursive: true);
    }

    private sealed class ShortReadStream(byte[] bytes) : Stream
    {
        private int _offset;
        public int Reads { get; private set; }
        public bool EofRead { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Reads++;
            int count = Math.Min(Math.Min(buffer.Length, 2), bytes.Length - _offset);
            bytes.AsMemory(_offset, count).CopyTo(buffer);
            _offset += count;
            EofRead |= count == 0;
            return ValueTask.FromResult(count);
        }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
