using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using NSubstitute;
using Romd.Application.Common.Content;
using Romd.Consumer.Application.Libraries;
using Romd.Consumer.Application.Vfs;
using Romd.Domain.Hashing;
using Romd.Domain.Source.Rom;
using Romd.Domain.Vfs;
using Romd.Infrastructure.Storage;
using Romd.Media;
using Romd.Media.Probe;
using Romd.Storage;
using Romd.PostgreSql.TestSupport;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Storage;

public sealed class ConvergedReadProbeEvidenceTests
{
    [Fact]
    public async Task ManagedDecoderComposition_UsesProductionDefaultsAndDoesNotClaimNativeChd()
    {
        await using var runtime = ConvergedReadDecoder.Managed();
        runtime.Options.MaxConcurrent.ShouldBe(4);
        runtime.Options.MaxIdle.ShouldBe(2);
        runtime.Options.CacheBytes.ShouldBe(64L << 20);
        runtime.Decoder.CanDecode(RomRepresentationEncoding.Rvz).ShouldBeTrue();
        runtime.Decoder.CanDecode(RomRepresentationEncoding.Chd).ShouldBeFalse();
        runtime.NativeSnapshot.ShouldBeNull();
        ReadResourceChecks.Create(runtime.Decoder).IsError.ShouldBeFalse();
        await runtime.DisposeAsync();
        await runtime.DisposeAsync();
    }

    [Fact]
    public async Task DecoderObserver_ArtifactOpen_ForwardsTheExactSourceAndContextWithoutOpeningRandomAccess()
    {
        var inner = NSubstitute.Substitute.For<IRomRepresentationDecoder>();
        var raw = NSubstitute.Substitute.For<ISynchronousRawContent>();
        await using var source = RomArtifactSource.FromSynchronousRaw(raw);
        var context = new RomMediaOpenContext(Sha256.Parse(new string('a', 64)), admissionTimeout: TimeSpan.FromMilliseconds(123));
        var decoded = NSubstitute.Substitute.For<IRandomAccessContent>();
        decoded.DisposeAsync().Returns(ValueTask.CompletedTask);
        using var cancellation = new CancellationTokenSource();
        inner.DecodeAsync(RomRepresentationEncoding.Chd, source, context, cancellation.Token)
            .Returns(Task.FromResult(decoded));
        var type = typeof(ConvergedReadChecks).GetNestedType("CountingDecoder", BindingFlags.NonPublic)!;
        var observer = (IRomRepresentationDecoder)Activator.CreateInstance(type,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null,
            [inner, NewState(), new Microsoft.AspNetCore.Http.HttpContextAccessor()], null)!;

        await using (var result = await observer.DecodeAsync(RomRepresentationEncoding.Chd, source, context, cancellation.Token))
        {
            await inner.Received(1).DecodeAsync(RomRepresentationEncoding.Chd, source, context, cancellation.Token);
            raw.DidNotReceive().Dispose();
        }
        await decoded.Received(1).DisposeAsync();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FullRead_TransientCapacity_EndToEndLatencyIncludesRejectedAttemptAndObservedBackoff(bool http)
    {
        await using var fixture = new FullReadFixture();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var watch = Stopwatch.StartNew();

        await fixture.ReadAsync(http, deadline.Token);

        watch.Stop();
        fixture.Attempts(http).ShouldBe(2);
        using var report = fixture.Report();
        var operation = report.RootElement.GetProperty("Operations").GetProperty(fixture.Operation(http));
        operation.GetProperty("Completed").GetInt64().ShouldBe(1);
        operation.GetProperty("Bytes").GetInt64().ShouldBe(fixture.Bytes.Length);
        var (started, finished) = fixture.AttemptWindow(http);
        started.ShouldBeGreaterThan(0L);
        finished.ShouldBeGreaterThan(started);
        double reported = operation.GetProperty("LatencyMilliseconds").GetProperty("MaximumMilliseconds").GetDouble();
        // Both attempt delays and the real retry wait are enclosed inside the probe's clock.
        // Reflection/JIT before that clock starts belongs only to the outer test stopwatch.
        reported.ShouldBeGreaterThanOrEqualTo(Stopwatch.GetElapsedTime(started, finished).TotalMilliseconds,
            "the reported full-read latency must include the rejected attempt and real wait, not just the successful attempt");
        reported.ShouldBeLessThanOrEqualTo(watch.Elapsed.TotalMilliseconds);
        report.RootElement.GetProperty("CapacityRejections").GetInt64().ShouldBe(1);
        report.RootElement.GetProperty("CapacityRetries").GetInt64().ShouldBe(1);
    }

    [Fact]
    public async Task FullRead_TransientHttpCapacity_ReportsContextualAttemptsAndActualWaitSeparatelyFromScheduledWait()
    {
        await using var fixture = new FullReadFixture();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        await fixture.ReadAsync(true, deadline.Token);

        using var report = fixture.Report();
        var retry = report.RootElement.GetProperty("Retries").GetProperty(fixture.Operation(true));
        retry.GetProperty("Attempts").GetInt64().ShouldBe(2);
        retry.GetProperty("HttpRejections").GetInt64().ShouldBe(1);
        retry.GetProperty("DecoderRejections").GetInt64().ShouldBe(0);
        retry.GetProperty("Retries").GetInt64().ShouldBe(1);
        retry.GetProperty("ScheduledWaitMilliseconds").GetProperty("MaximumMilliseconds").GetDouble().ShouldBe(350);
        retry.GetProperty("ObservedWaitMilliseconds").GetProperty("MaximumMilliseconds").GetDouble()
            .ShouldBeGreaterThanOrEqualTo(300, "a scheduled delay is not an observed admission or retry wait");
    }

    [Fact]
    public void BeginLoad_WarmupBytesAndOperations_DoNotInflateSustainedThroughput()
    {
        object state = NewState();
        Record(state, "client-0/full/gc-1.iso", 4096, 10);
        Record(state, "client-0/range/gc-1.iso", 65536, 5);
        state.GetType().GetMethod("BeginLoad")!.Invoke(state, null);
        Record(state, "client-0/full/gc-1.iso", 1024, 20);
        Record(state, "client-0/range/gc-1.iso", 17, 7);
        // A batch describes the same verified range bytes and must not count those bytes a second time.
        Record(state, "client-0/ranges/gc-1.iso", 17, 8);

        using var report = Report(state);
        var sustained = report.RootElement.GetProperty("SustainedOperations");
        sustained.GetProperty("client-0/full/gc-1.iso").GetProperty("Completed").GetInt64().ShouldBe(1);
        sustained.GetProperty("client-0/full/gc-1.iso").GetProperty("Bytes").GetInt64().ShouldBe(1024);
        sustained.GetProperty("client-0/range/gc-1.iso").GetProperty("Bytes").GetInt64().ShouldBe(17);
        var delivered = report.RootElement.GetProperty("SustainedDeliveredBytesByClientAndFormat");
        delivered.GetProperty("client-0/original").GetInt64().ShouldBe(1041);
        double seconds = Math.Max(report.RootElement.GetProperty("LoadElapsedSeconds").GetDouble(), .001);
        double rate = sustained.GetProperty("client-0/full/gc-1.iso").GetProperty("MiBPerSecond").GetDouble();
        (rate * seconds * 1024 * 1024).ShouldBe(1024, .001);
        report.RootElement.GetProperty("Operations").GetProperty("client-0/full/gc-1.iso")
            .GetProperty("Bytes").GetInt64().ShouldBe(5120, "lifetime bytes remain separately observable");
    }

    [Fact]
    public void Latency_ManySamples_RetainsFixedStorageAndReportsP99()
    {
        var type = typeof(ConvergedReadChecks).GetNestedType("Latency", BindingFlags.NonPublic)!;
        object histogram = Activator.CreateInstance(type, true)!;
        var add = type.GetMethod("Add")!;
        for (int index = 0; index < 10000; index++)
            add.Invoke(histogram, [index < 9900 ? 1d : 100d]);

        using var report = JsonSerializer.SerializeToDocument(type.GetMethod("Report")!.Invoke(histogram, null));

        report.RootElement.GetProperty("Count").GetInt64().ShouldBe(10000);
        report.RootElement.GetProperty("P99UpperBoundMilliseconds").GetDouble().ShouldBeInRange(1, 1.011);
        var bins = (long[])type.GetField("_bins", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(histogram)!;
        bins.Length.ShouldBe(2048, "latency storage cannot grow with the number of read requests");
    }

    [Fact]
    public async Task ForwardingStore_AllPorts_DelegateFactsAndCancellationWithoutOwningCallerStore()
    {
        var inner = Substitute.For<IContentAddressableStore>();
        object state = NewState();
        var accessor = new HttpContextAccessor();
        await using var spy = NewStoreSpy(inner, state, accessor);
        using var cancellation = new CancellationTokenSource();
        CancellationToken ct = cancellation.Token;
        var key = StorageKey.FromHash(Sha256.Parse(new string('a', 64)));
        using var source = new MemoryStream([1, 2, 3]);
        using var retrieved = new MemoryStream([4, 5, 6]);
        var progress = new Progress<StoreProgress>();
        var staged = Substitute.For<IStagedContent>();
        var stored = new StoreResult(key, 3, 3, false, false);
        var info = new ContentInfo(key, 3, DateTimeOffset.UnixEpoch, false);
        inner.StageAsync(source, key, progress, ct).Returns(staged);
        inner.StoreAsync(source, progress, ct).Returns(stored);
        inner.RetrieveAsync(key, ct).Returns(retrieved);
        inner.VerifyAsync(key, ct).Returns(true);
        inner.CleanupTemporaryFilesAsync(ct).Returns(2);
        inner.ExistsAsync(key, ct).Returns(true);
        inner.EnumerateAsync(ct).Returns(KeysAsync(key, ct));
        inner.DeleteAsync(key, ct).Returns(true);
        inner.GetInfoAsync(key, ct).Returns(info);

        (await spy.StageAsync(source, key, progress, ct)).ShouldBeSameAs(staged);
        (await spy.StoreAsync(source, progress, ct)).ShouldBe(stored);
        (await spy.RetrieveAsync(key, ct)).ShouldBeSameAs(retrieved);
        (await spy.VerifyAsync(key, ct)).ShouldBeTrue();
        (await spy.CleanupTemporaryFilesAsync(ct)).ShouldBe(2);
        (await spy.ExistsAsync(key, ct)).ShouldBeTrue();
        var keys = new List<StorageKey>();
        await foreach (var item in spy.EnumerateAsync(ct))
            keys.Add(item);
        keys.ShouldBe([key]);
        (await spy.DeleteAsync(key, ct)).ShouldBeTrue();
        (await spy.GetInfoAsync(key, ct)).ShouldBe(info);
        await spy.DisposeAsync();
        await inner.DidNotReceive().DisposeAsync();
        ((long)state.GetType().GetField("CasOpens")!.GetValue(state)!).ShouldBe(0,
            "non-positional delegation must not invent positional-read evidence");
    }

    [Fact]
    public async Task ForwardingStore_PositionalErrorsCancellationAndMetadataCounting_PreserveBoundary()
    {
        var inner = Substitute.For<IContentAddressableStore>();
        object state = NewState();
        var context = new DefaultHttpContext();
        context.Request.Headers["X-Romd-Probe-Metadata"] = "1";
        await using var spy = NewStoreSpy(inner, state, new HttpContextAccessor { HttpContext = context });
        var key = StorageKey.FromHash(Sha256.Parse(new string('a', 64)));
        var failure = new IOException("fixture CAS failure");
        inner.OpenRandomAccessAsync(key, CancellationToken.None).Returns(Task.FromException<IRandomAccessContent?>(failure));
        Exception? caught = null;
        try
        { await spy.OpenRandomAccessAsync(key, CancellationToken.None); }
        catch (Exception error) { caught = error; }
        caught.ShouldBeSameAs(failure);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        inner.OpenRandomAccessAsync(key, cancelled.Token).Returns(Task.FromCanceled<IRandomAccessContent?>(cancelled.Token));
        OperationCanceledException? cancellation = null;
        try
        { await spy.OpenRandomAccessAsync(key, cancelled.Token); }
        catch (OperationCanceledException error) { cancellation = error; }
        cancellation.ShouldNotBeNull();
        cancellation!.CancellationToken.ShouldBe(cancelled.Token);
        ((long)state.GetType().GetField("CasOpens")!.GetValue(state)!).ShouldBe(2);
        ((long)state.GetType().GetField("MetadataCasOpens")!.GetValue(state)!).ShouldBe(2);
    }

    [Fact]
    public async Task ForwardingStore_PositionalSuccess_PreservesReaderFactsAndCountsActualBytes()
    {
        var inner = Substitute.For<IContentAddressableStore>();
        object state = NewState();
        await using var spy = NewStoreSpy(inner, state, new HttpContextAccessor());
        var key = StorageKey.FromHash(Sha256.Parse(new string('a', 64)));
        var source = new BytesContent([0, 1, 2, 3, 4, 5, 6, 7]);
        inner.OpenRandomAccessAsync(key, CancellationToken.None).Returns(source);

        var opened = (await spy.OpenRandomAccessAsync(key, CancellationToken.None))!;
        try
        {
            opened.Length.ShouldBe(8);
            opened.Integrity.ShouldBe(ContentReadIntegrity.None);
            byte[] bytes = new byte[4];
            (await opened.ReadAtAsync(2, bytes, CancellationToken.None)).ShouldBe(4);
            bytes.ShouldBe([2, 3, 4, 5]);
        }
        finally { await opened.DisposeAsync(); }

        source.DisposeCount.ShouldBe(1);
        using var report = Report(state);
        report.RootElement.GetProperty("DataflowBytes").GetProperty("cas/encoded-artifact-read").GetInt64().ShouldBe(4);
        report.RootElement.GetProperty("CasOpens").GetInt64().ShouldBe(1);
    }

    [Fact]
    public async Task FixedRepository_FrozenFilesAndScopedLookup_RejectsUnsupportedCallsAndCancelledReads()
    {
        var hash = Sha256.Parse(new string('a', 64));
        var file = new ConsumerVfsFile("gc-1.iso", hash, 12);
        var files = new Dictionary<string, ConsumerVfsFile> { [file.RelativePath] = file };
        var type = typeof(ConvergedReadChecks).GetNestedType("FixedRepository", BindingFlags.NonPublic)!;
        var repository = (IConsumerVfsRepository)Activator.CreateInstance(type,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, [files], null)!;
        var users = (Guid[])typeof(ConvergedReadChecks).GetField("Users", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        var scope = new ConsumerLibraryScope(users[0]);
        files.Clear();

        var found = (await repository.GetFileAsync(scope, 1, file.RelativePath, CancellationToken.None, VfsFormat.Stored))
            .ShouldBeOfType<ConsumerLibraryReadResult<ConsumerVfsResolvedFile>.Found>();
        found.Value.File.ShouldBeSameAs(file);
        (await repository.GetFileAsync(scope, 2, file.RelativePath, CancellationToken.None))
            .ShouldBeOfType<ConsumerLibraryReadResult<ConsumerVfsResolvedFile>.ItemNotFound>();
        (await repository.GetFileAsync(new(Guid.NewGuid()), 1, file.RelativePath, CancellationToken.None))
            .ShouldBeOfType<ConsumerLibraryReadResult<ConsumerVfsResolvedFile>.ItemNotFound>();
        (await repository.GetFileAsync(scope, 1, file.RelativePath, CancellationToken.None, (VfsFormat)999))
            .ShouldBeOfType<ConsumerLibraryReadResult<ConsumerVfsResolvedFile>.ItemNotFound>();
        (await repository.ListPlatformsAsync(scope, ct: CancellationToken.None))
            .ShouldBeOfType<ConsumerLibraryReadResult<ConsumerVfsPlatformPage>.Rejected>();
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        OperationCanceledException? error = null;
        try
        { await repository.GetFileAsync(scope, 1, file.RelativePath, cancelled.Token); }
        catch (OperationCanceledException caught) { error = caught; }
        error.ShouldNotBeNull();
        error!.CancellationToken.ShouldBe(cancelled.Token);
    }

    [Fact]
    public async Task RangesAsync_TransientCapacityAtLaterRange_RetriesOnlyRejectedRequest()
    {
        await using var fixture = await RangeFixture.CreateAsync(false, TimeSpan.FromSeconds(1), CancellationToken.None);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        await fixture.ReadAsync(deadline.Token);

        fixture.Handler.CallsAt(0).ShouldBe(1, "a verified earlier range must not be replayed after later HTTP backpressure");
        fixture.Handler.CallsAt(RangeHandler.RejectedOffset).ShouldBe(2);
        fixture.Handler.Rejected.ShouldBe(1);
        fixture.Handler.SuccessfulOffsets.ShouldBeGreaterThan(10, "all deterministic and random ranges, including EOF, remain exercised");
        fixture.CompletedBatches.ShouldBe(1);
    }

    [Fact]
    public async Task RangesAsync_PermanentCapacityAtLaterRange_FailsBoundedlyWithoutReplayingVerifiedRanges()
    {
        // This typed response double uses a positive fractional Delta to exercise the existing retry-count bound quickly.
        // It qualifies the retry algorithm, not serialization of Retry-After on a real HTTP connection.
        await using var fixture = await RangeFixture.CreateAsync(true, TimeSpan.FromMilliseconds(1), CancellationToken.None);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        Exception? failure = null;

        try
        { await fixture.ReadAsync(deadline.Token); }
        catch (Exception error) { failure = error; }

        failure.ShouldNotBeNull();
        failure!.GetType().Name.ShouldBe("HttpCapacityException", "permanent no-progress capacity cannot become successful evidence");
        fixture.Handler.Rejected.ShouldBe(121, "the existing maximum of 120 retries must remain enforced");
        fixture.Handler.CallsAt(0).ShouldBe(1, "completed range progress must survive rejected later requests");
        fixture.CompletedBatches.ShouldBe(0);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RunAsync_ExistingOutputOrTemporaryPath_PreservesExistingBytes(bool temporaryCollision)
    {
        string root = Directory.CreateTempSubdirectory("romd-read-evidence-").FullName;
        try
        {
            string report = Path.Combine(root, "report.json");
            string preserved = temporaryCollision ? report + ".next" : report;
            byte[] original = [0, 1, 2, 3, 255];
            await File.WriteAllBytesAsync(preserved, original, CancellationToken.None);
            var hash = Sha256.Parse(new string('a', 64));
            var identity = MediaFactFixtures.Identity(new byte[4096]);
            var library = new (RomContentDescriptor, string, string)[]
            {
                (MeasuredContent(1, identity, MediaFactFixtures.Rvz(identity.Size, 1, 5, 19, 131072, false), hash, 128), preserved, "gc"),
                (MeasuredContent(2, identity, MediaFactFixtures.Rvz(identity.Size, 2, 5, 19, 131072, true), hash, 128), preserved, "wii")
            };
            Exception? failure = null;
            try
            {
                await ConvergedReadChecks.RunAsync(Substitute.For<IContentAddressableStore>(),
                    Substitute.For<IRomContentCatalog>(), library, RomMediaPolicyComposition.Create(), ProbeMediaFacts.ManagedAdmission, ConvergedReadDecoder.Managed(),
                    ConvergedReadProfile.NintendoWholeFiles, TimeSpan.FromMinutes(30), report, CancellationToken.None);
            }
            catch (Exception error) { failure = error; }
            File.Exists(preserved).ShouldBeTrue("qualification reports must never replace an existing fixture or temporary path");
            (await File.ReadAllBytesAsync(preserved, CancellationToken.None)).ShouldBe(original);
            failure.ShouldNotBeNull();
            if (!temporaryCollision)
                failure.ShouldBeOfType<IOException>();
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task SaturationAsync_PendingReaderGrantedBeforeObservationFailure_DisposesEveryOwnedReader()
    {
        object state = NewState();
        var identity = MediaFactFixtures.Identity(new byte[1024]);
        var hash = identity.Sha256;
        var content = MeasuredContent(1, identity, MediaFactFixtures.Rvz(identity.Size, 1, 5, 19, 131072, false), hash, 1024);
        var target = OriginalTarget(content);
        SetEntry(state, target);
        var catalog = Substitute.For<IRomContentCatalog>();
        catalog.FindByRomIdAsync(1, Arg.Any<CancellationToken>()).Returns(content);
        var admission = Substitute.For<IRomChdDeliveryAdmission>();
        admission.AssessManaged(content.Preferred).Returns(content.Preferred.BindManaged(ProbeDecoder));
        var cas = Substitute.For<IContentAddressableStore>();
        cas.OpenRandomAccessAsync(StorageKey.FromHash(hash), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult<IRandomAccessContent?>(new BytesContent(new byte[1024])));
        var decoder = new EarlyGrantDecoder();
        // This decoder-port fixture advances the existing observation at the admission boundary.
        // Its controlled grant races the pending-state assertion without changing the production wrapper.
        decoder.ObserveAdmission = () =>
        {
            var field = state.GetType().GetField("DecoderOpens")!;
            field.SetValue(state, (long)field.GetValue(state)! + 1);
        };
        var reader = new RomContentReader(catalog, cas, decoder, admission);
        var http = new SaturationMetadataHandler(hash, 1024, identity.Md5, identity.Sha1);
        using var client = new HttpClient(http) { BaseAddress = new Uri("http://probe.invalid") };
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        Exception? failure = null;
        try
        {
            await (Task)typeof(ConvergedReadChecks).GetMethod("SaturationAsync", BindingFlags.Static | BindingFlags.NonPublic)!
                .Invoke(null, [reader, cas, new[] { client }, state.GetType().GetProperty("Entries")!.GetValue(state), state, deadline.Token])!;
        }
        catch (Exception error) { failure = error; }
        try
        {
            failure.ShouldBeOfType<InvalidOperationException>();
            failure!.Message.ShouldContain("export-style waiter reached decoder admission and remains pending");
            decoder.GrantedCount.ShouldBe(5, "the pending reader is actually admitted before the observation failure");
            decoder.ActiveCount.ShouldBe(0, "every returned reader, including an unobserved admitted result, must be disposed");
            decoder.DisposedCount.ShouldBe(5);
            http.FirstContentRange.ShouldBe("bytes=0-65535", "the saturation probe must reject an actual Range request before payload admission");
        }
        finally { await decoder.DisposeAsync(); }
    }

    [Fact]
    public async Task WorkerAsync_ValidatedHttpExhaustion_AdvancesNextEntryWithExplicitPartialDeferral()
    {
        // Positive fractional Retry-After exercises the unchanged 120-retry count bound quickly.
        // It does not qualify Retry-After wire serialization or a real 120-second overload window.
        await using var fixture = await RangeFixture.CreateAsync(true, TimeSpan.FromMilliseconds(1), CancellationToken.None);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var run = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
        Exception? failure = null;
        try
        { await fixture.WorkerAsync(run, deadline.Token); }
        catch (Exception error) { failure = error; }

        failure.ShouldBeNull("terminal validated pre-body HTTP503 is a distinct lane deferral, not a whole-load abort");
        fixture.Handler.Rejected.ShouldBe(121);
        fixture.Handler.CallsAt(0).ShouldBe(2, "one initial verified range per entry, with no replay of the first entry");
        fixture.NextEntryCompleted.ShouldBeTrue("the unpaced lane must advance to another entry after retry exhaustion");
        run.IsCancellationRequested.ShouldBeFalse("a lane deferral must not cancel sibling workers");
        using var report = fixture.Report();
        var operations = report.RootElement.GetProperty("SustainedOperations");
        operations.GetProperty("client-0/ranges/gc-1.iso").GetProperty("Completed").GetInt64().ShouldBe(0);
        operations.GetProperty("client-0/ranges/gc-1.iso").GetProperty("Failed").GetInt64().ShouldBe(0);
        var partial = operations.GetProperty("client-0/range/gc-1.iso");
        partial.GetProperty("Completed").GetInt64().ShouldBe(1);
        partial.GetProperty("Bytes").GetInt64().ShouldBe(65536, "verified partial bytes count once even though the batch is incomplete");
        operations.GetProperty("client-0/ranges/wii-2.iso").GetProperty("Completed").GetInt64().ShouldBe(1);
        var lane = report.RootElement.GetProperty("LaneProgress").GetProperty("http-0-original-ranges");
        lane.GetProperty("Started").GetInt64().ShouldBe(2);
        lane.GetProperty("Completed").GetInt64().ShouldBe(1);
        lane.GetProperty("Deferred").GetInt64().ShouldBe(1);
        lane.GetProperty("Failed").GetInt64().ShouldBe(0);
        lane.GetProperty("InFlight").GetInt64().ShouldBe(0);
        var deferred = report.RootElement.GetProperty("AdmissionDeferrals").GetProperty("client-0/ranges/gc-1.iso");
        deferred.GetProperty("Count").GetInt64().ShouldBe(1);
        deferred.GetProperty("VerifiedPartialBytes").GetInt64().ShouldBe(65536);
        deferred.GetProperty("RejectedRangeOffset").GetInt64().ShouldBe(RangeHandler.RejectedOffset);
        deferred.GetProperty("ElapsedMilliseconds").GetProperty("MaximumMilliseconds").GetDouble().ShouldBeGreaterThan(0);
        deferred.GetProperty("Retries").GetInt64().ShouldBe(120);
        var delivered = report.RootElement.GetProperty("SustainedDeliveredBytesByClientAndFormat");
        long rangeBytes = operations.EnumerateObject().Where(item => item.Name.Contains("/range/", StringComparison.Ordinal))
            .Sum(item => item.Value.GetProperty("Bytes").GetInt64());
        delivered.GetProperty("client-0/original").GetInt64().ShouldBe(rangeBytes,
            "enclosing complete or deferred batches must not double-count individually verified ranges");
    }

    [Fact]
    public async Task WorkerAsync_FullHttpTerminalDeferral_HasZeroVerifiedBytesAndAdvancesNextEntry()
    {
        object state = NewState();
        byte[] bytes = [0, 1, 2, 3, 255];
        var hash = Sha256.FromSpan(SHA256.HashData(bytes));
        var target = new RomDeliveryTarget(1, 1, RomDeliveryFormat.Original, "disc.iso", hash, bytes.Length,
            hash, bytes.Length, hash, bytes.Length, RomRepresentationEncoding.Identity);
        var first = SetEntry(state, target);
        var type = first.GetType();
        var secondTarget = target with { RomId = 2 };
        var secondFile = new ConsumerVfsFile("wii-2.iso", hash, bytes.Length, Target: secondTarget);
        var second = Activator.CreateInstance(type, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            null, [secondTarget, "oracle.iso", "wii", "wii-2.iso", secondFile], null)!;
        var entries = Array.CreateInstance(type, 2);
        entries.SetValue(first, 0);
        entries.SetValue(second, 1);
        state.GetType().GetProperty("Entries")!.SetValue(state, entries);
        state.GetType().GetProperty("Phase")!.SetValue(state, "sustained-load");
        state.GetType().GetMethod("BeginLoad")!.Invoke(state, null);
        var handler = new AdmissionHandler(bytes, hash);
        using var client = new HttpClient(handler) { BaseAddress = new Uri("http://probe.invalid") };
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var run = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
        var full = typeof(ConvergedReadChecks).GetMethod("FullHttpAsync", BindingFlags.Static | BindingFlags.NonPublic)!;
        var operation = EntryDelegate(async (entry, sequence, token) =>
        {
            await (Task)full.Invoke(null, [client, entry, state, 0, token])!;
            if (sequence == 1)
                EndFixtureLoad(state);
        });

        await InvokeWorkerAsync(state, run, operation, "full-deferral", "client-0/full", deadline.Token);

        handler.Rejected.ShouldBe(121);
        handler.Successful.ShouldBe(1);
        run.IsCancellationRequested.ShouldBeFalse();
        using var report = Report(state);
        var operations = report.RootElement.GetProperty("SustainedOperations");
        operations.GetProperty("client-0/full/gc-1.iso").GetProperty("Completed").GetInt64().ShouldBe(0);
        operations.GetProperty("client-0/full/gc-1.iso").GetProperty("Deferred").GetInt64().ShouldBe(1);
        operations.GetProperty("client-0/full/gc-1.iso").GetProperty("Bytes").GetInt64().ShouldBe(0);
        operations.GetProperty("client-0/full/wii-2.iso").GetProperty("Completed").GetInt64().ShouldBe(1);
        report.RootElement.GetProperty("SustainedDeliveredBytesByClientAndFormat")
            .GetProperty("client-0/original").GetInt64().ShouldBe(bytes.Length);
        var deferred = report.RootElement.GetProperty("AdmissionDeferrals").GetProperty("client-0/full/gc-1.iso");
        deferred.GetProperty("VerifiedPartialBytes").GetInt64().ShouldBe(0);
        deferred.GetProperty("RejectedRangeOffset").ValueKind.ShouldBe(JsonValueKind.Null);
        deferred.GetProperty("Attempts").GetInt64().ShouldBe(121);
        deferred.GetProperty("Retries").GetInt64().ShouldBe(120);
        report.RootElement.GetProperty("LaneProgress").GetProperty("full-deferral")
            .GetProperty("DeferredEndToEndMilliseconds").GetProperty("Count").GetInt64().ShouldBe(1);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WorkerAsync_UnexhaustedOrMixedCapacitySequence_NeverQualifiesHttpDeferral(bool mixed)
    {
        object state = NewState();
        state.GetType().GetProperty("Phase")!.SetValue(state, "sustained-load");
        state.GetType().GetMethod("BeginLoad")!.Invoke(state, null);
        using var run = new CancellationTokenSource();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var client = new HttpClient(new AdmissionHandler([0], Sha256.Parse(new string('a', 64))))
        { BaseAddress = new Uri("http://probe.invalid") };
        var send = typeof(ConvergedReadChecks).GetMethod("SendContentAsync", BindingFlags.Static | BindingFlags.NonPublic)!;
        var retry = typeof(ConvergedReadChecks).GetMethod("RetryAsync", BindingFlags.Static | BindingFlags.NonPublic)!;
        int attempts = 0;
        async Task AttemptAsync(CancellationToken token)
        {
            if (++attempts == 1 && mixed)
                throw new ResourceCapacityException("mixed sequence includes a decoder rejection");
            using var request = new HttpRequestMessage(HttpMethod.Get, "http://probe.invalid/gc-1.iso");
            using var response = await (Task<HttpResponseMessage>)send.Invoke(null, [client, request, state, token])!;
        }
        var operation = EntryDelegate((_, _, token) => mixed
            ? (Task)retry.Invoke(null, [new Func<CancellationToken, Task>(AttemptAsync), state, token, "client-0/full/gc-1.iso"])!
            : AttemptAsync(token));
        Exception? failure = null;
        try
        { await InvokeWorkerAsync(state, run, operation, "not-http-only-exhaustion", "client-0/full", deadline.Token); }
        catch (Exception error) { failure = error; }

        failure.ShouldNotBeNull().GetType().Name.ShouldBe("HttpCapacityException");
        attempts.ShouldBe(mixed ? 121 : 1);
        run.IsCancellationRequested.ShouldBeTrue();
        using var report = Report(state);
        report.RootElement.GetProperty("AdmissionDeferrals").EnumerateObject().ShouldBeEmpty();
        var lane = report.RootElement.GetProperty("LaneProgress").GetProperty("not-http-only-exhaustion");
        lane.GetProperty("Deferred").GetInt64().ShouldBe(0);
        lane.GetProperty("Failed").GetInt64().ShouldBe(1);
    }

    [Theory]
    [InlineData("decoder-capacity")]
    [InlineData("corruption")]
    [InlineData("transport")]
    [InlineData("caller-cancellation")]
    [InlineData("operation-deadline")]
    public async Task WorkerAsync_GenuineFailure_NeverBecomesAdmissionDeferral(string kind)
    {
        object state = NewState();
        state.GetType().GetMethod("BeginLoad")!.Invoke(state, null);
        using var run = new CancellationTokenSource();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        Exception primary = kind switch
        {
            "decoder-capacity" => new ResourceCapacityException("direct decoder capacity is a load failure"),
            "corruption" => new InvalidDataException("verified bytes differ"),
            "transport" => new IOException("body transport failed"),
            "caller-cancellation" => new OperationCanceledException(deadline.Token),
            _ => new OperationCanceledException("600-second operation deadline", new CancellationToken(true))
        };
        var operation = EntryDelegate((_, _, _) => Task.FromException(primary));
        Exception? caught = null;
        try
        { await InvokeWorkerAsync(state, run, operation, "genuine-failure", "client-0/full", deadline.Token); }
        catch (Exception error) { caught = error; }

        caught.ShouldBeSameAs(primary);
        run.IsCancellationRequested.ShouldBeTrue();
        using var report = Report(state);
        var lane = report.RootElement.GetProperty("LaneProgress").GetProperty("genuine-failure");
        lane.GetProperty("Completed").GetInt64().ShouldBe(0);
        lane.GetProperty("Failed").GetInt64().ShouldBe(1);
        lane.GetProperty("InFlight").GetInt64().ShouldBe(0);
        if (lane.TryGetProperty("Deferred", out var deferred))
            deferred.GetInt64().ShouldBe(0);
    }

    [Theory]
    [InlineData("body")]
    [InlineData("declared-body")]
    [InlineData("etag")]
    [InlineData("content-range")]
    [InlineData("missing-retry")]
    [InlineData("zero-retry")]
    [InlineData("oversized-retry")]
    public async Task SendContentAsync_Unvalidated503_RemainsFailureNotTypedAdmissionRejection(string kind)
    {
        object state = NewState();
        var response = new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
        {
            Content = new ByteArrayContent(kind == "body" ? [255] : [])
        };
        if (kind == "declared-body")
            response.Content.Headers.ContentLength = 1;
        if (kind == "etag")
            response.Headers.ETag = new EntityTagHeaderValue("\"wrong\"");
        if (kind == "content-range")
            response.Content.Headers.ContentRange = new ContentRangeHeaderValue(0, 0, 1024);
        if (kind != "missing-retry")
            response.Headers.RetryAfter = new RetryConditionHeaderValue(kind switch
            {
                "zero-retry" => TimeSpan.Zero,
                "oversized-retry" => TimeSpan.FromSeconds(31),
                _ => TimeSpan.FromMilliseconds(1)
            });
        using var client = new HttpClient(new FixedResponseHandler(response));
        using var request = new HttpRequestMessage(HttpMethod.Get, "http://probe.invalid/disc.iso");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        Exception? failure = null;
        try
        {
            var sent = (Task<HttpResponseMessage>)typeof(ConvergedReadChecks).GetMethod("SendContentAsync",
                BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [client, request, state, deadline.Token])!;
            using var unexpected = await sent;
        }
        catch (Exception error) { failure = error; }

        failure.ShouldBeOfType<InvalidOperationException>("only a validated bodyless pre-body503 can enter capacity retry or deferral");
    }

    [Theory]
    [InlineData("unauthorized")]
    [InlineData("forbidden")]
    [InlineData("etag")]
    [InlineData("length")]
    [InlineData("hash")]
    public async Task WorkerAsync_RealHttpStatusHeadersOrHashFailure_RemainsFatal(string kind)
    {
        object state = NewState();
        byte[] original = [0, 1, 2, 3, 255];
        var hash = Sha256.FromSpan(SHA256.HashData(original));
        var target = new RomDeliveryTarget(1, 1, RomDeliveryFormat.Original, "disc.iso", hash, original.Length,
            hash, original.Length, hash, original.Length, RomRepresentationEncoding.Identity);
        SetEntry(state, target);
        state.GetType().GetMethod("BeginLoad")!.Invoke(state, null);
        var response = new HttpResponseMessage(kind switch
        {
            "unauthorized" => HttpStatusCode.Unauthorized,
            "forbidden" => HttpStatusCode.Forbidden,
            _ => HttpStatusCode.OK
        })
        { Content = new ByteArrayContent(kind == "hash" ? [4, 3, 2, 1, 0] : original) };
        response.Headers.ETag = new EntityTagHeaderValue("\"" + (kind == "etag" ? new string('a', 64) : hash.ToString()) + "\"");
        if (kind == "length")
            response.Content.Headers.ContentLength = original.Length + 1;
        using var client = new HttpClient(new FixedResponseHandler(response)) { BaseAddress = new Uri("http://probe.invalid") };
        using var run = new CancellationTokenSource();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var method = typeof(ConvergedReadChecks).GetMethod("FullHttpAsync", BindingFlags.Static | BindingFlags.NonPublic)!;
        var operation = EntryDelegate((entry, _, token) => (Task)method.Invoke(null, [client, entry, state, 0, token])!);
        Exception? caught = null;
        try
        { await InvokeWorkerAsync(state, run, operation, "http-fatal", "client-0/full", deadline.Token); }
        catch (Exception error) { caught = error; }

        caught.ShouldBeOfType<InvalidOperationException>();
        run.IsCancellationRequested.ShouldBeTrue();
        using var report = Report(state);
        var operationReport = report.RootElement.GetProperty("SustainedOperations").GetProperty("client-0/full/gc-1.iso");
        operationReport.GetProperty("Completed").GetInt64().ShouldBe(0);
        operationReport.GetProperty("Bytes").GetInt64().ShouldBe(0);
        operationReport.GetProperty("Failed").GetInt64().ShouldBe(1);
        var lane = report.RootElement.GetProperty("LaneProgress").GetProperty("http-fatal");
        lane.GetProperty("Failed").GetInt64().ShouldBe(1);
        if (lane.TryGetProperty("Deferred", out var deferred))
            deferred.GetInt64().ShouldBe(0);
    }

    [Fact]
    public void ValidateCompletedCoverage_WarmupOrPartialRanges_CannotQualifyMissingSustainedBatch()
    {
        object state = NewState();
        foreach (string lane in new[] { "direct", "client-0", "client-1" })
            foreach (string operation in new[] { "full", "ranges" })
                Record(state, $"{lane}/{operation}/gc-1.iso", 1024, 10);
        state.GetType().GetMethod("BeginLoad")!.Invoke(state, null);
        foreach (string lane in new[] { "direct", "client-0", "client-1" })
        {
            Record(state, $"{lane}/full/gc-1.iso", 1024, 10);
            if (lane != "client-1")
                Record(state, $"{lane}/ranges/gc-1.iso", 1024, 10);
        }
        Record(state, "client-1/range/gc-1.iso", 17, 10);

        var rejected = Should.Throw<TargetInvocationException>(() => typeof(ConvergedReadChecks)
            .GetMethod("ValidateCompletedCoverage", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, [state.GetType().GetProperty("Entries")!.GetValue(state), 2, state]));

        rejected.InnerException.ShouldBeOfType<InvalidOperationException>();
        rejected.InnerException!.Message.ShouldContain("each independent client completed");
        using var report = Report(state);
        report.RootElement.GetProperty("SustainedOperations").GetProperty("client-1/ranges/gc-1.iso")
            .GetProperty("Completed").GetInt64().ShouldBe(0);
    }

    [Theory]
    [InlineData("none")]
    [InlineData("writer")]
    [InlineData("sampler")]
    [InlineData("cancel")]
    public async Task ReportFailureAsync_JoinedTerminalResourcesAndEvidence_PreservePrimary(string secondaryFailure)
    {
        string root = Directory.CreateTempSubdirectory("romd-read-failure-terminal-").FullName;
        await using var decoder = new RomRepresentationDecoder(new RomMediaAdapterRegistry(new PassThroughAdapter()), new());
        var resources = ReadResourceChecks.Create(decoder).Value;
        object state = NewState();
        state.GetType().GetProperty("Resources")!.SetValue(state, resources);
        state.GetType().GetProperty("Phase")!.SetValue(state, "sustained-load");
        state.GetType().GetMethod("BeginLoad")!.Invoke(state, null);
        state.GetType().GetMethod("StartOperation")!.Invoke(state, ["direct/full/gc-1.iso"]);
        using var run = new CancellationTokenSource();
        using var sampling = new CancellationTokenSource();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var samplerCancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var workerCancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var workers = (List<Task>)state.GetType().GetProperty("Workers")!.GetValue(state)!;
        bool workersJoinedBeforeSamplerCancel = false;
        using var registration = sampling.Token.Register(() =>
        {
            workersJoinedBeforeSamplerCancel = workers.All(task => task.IsCompleted);
            samplerCancelled.TrySetResult();
        });
        using var workerCancellation = run.Token.Register(() => workerCancelled.TrySetResult());
        using var cancelFailure = run.Token.Register(() =>
        {
            if (secondaryFailure == "cancel")
                throw new IOException("secondary cancellation callback failed");
        });
        var leased = await decoder.DecodeAsync(RomRepresentationEncoding.Rvz, new BytesContent(new byte[1024]),
            deadline.Token, Sha256.Parse(new string('a', 64)));
        var bufferType = typeof(ConvergedReadChecks).GetNestedType("BufferLease", BindingFlags.NonPublic)!;
        var buffer = (IDisposable)Activator.CreateInstance(bufferType,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, [state, 1024], null)!;
        resources.Sample("sustained-load").Value.ActiveDecoderSlots.ShouldBe(1);
        var primary = new IOException("primary body read failure");
        bool joined = false;
        async Task ReleaseAssetsAsync(CancellationToken ct)
        {
            await Task.Delay(25, ct);
            await leased.DisposeAsync();
            buffer.Dispose();
            state.GetType().GetMethod("EndOperation")!.Invoke(state, ["direct/full/gc-1.iso"]);
            resources.Sample("sustained-load").IsError.ShouldBeFalse();
        }
        async Task SettleWorkerAsync(CancellationToken ct)
        {
            await workerCancelled.Task.WaitAsync(ct);
            await ReleaseAssetsAsync(ct);
        }
        if (secondaryFailure == "cancel")
            workers.Add(SettleWorkerAsync(deadline.Token));
        async Task SettleSamplerAsync(CancellationToken ct)
        {
            await samplerCancelled.Task.WaitAsync(ct);
            if (secondaryFailure == "cancel")
                await Task.WhenAll(workers).WaitAsync(ct);
            else
                await ReleaseAssetsAsync(ct);
            joined = true;
            if (secondaryFailure == "sampler")
                throw new IOException("secondary sampler failed");
        }
        var sample = SettleSamplerAsync(deadline.Token);
        string reportPath = Path.Combine(root, "failure.json");
        if (secondaryFailure == "writer")
            Directory.CreateDirectory(reportPath);
        Exception? failure = null;
        try
        {
            try
            {
                await (Task)typeof(ConvergedReadChecks).GetMethod("ReportFailureAsync", BindingFlags.Static | BindingFlags.NonPublic)!
                    .Invoke(null, [primary, run, sampling, sample, state, reportPath, deadline.Token])!;
            }
            catch (Exception error) { failure = error; }
            failure.ShouldBeSameAs(primary, "writer or sampler failure must never replace the original read error");
            joined.ShouldBeTrue("sampling and eligible lease cleanup finish before terminal evidence is published");
            workersJoinedBeforeSamplerCancel.ShouldBeTrue("tracked workers must be joined before sampler shutdown and terminal observations");
            if (secondaryFailure is "none" or "cancel")
            {
                using var report = JsonDocument.Parse(await File.ReadAllTextAsync(reportPath, deadline.Token));
                report.RootElement.GetProperty("Status").GetString().ShouldBe("failed");
                report.RootElement.GetProperty("ObservedResources").GetProperty("Latest")
                    .GetProperty("ActiveDecoderSlots").GetInt32().ShouldBe(0, "a stale in-flight snapshot cannot be terminal evidence");
                report.RootElement.GetProperty("CurrentLeasedBufferBytes").GetInt64().ShouldBe(0);
                report.RootElement.GetProperty("TerminalResourcesConfirmed").GetBoolean().ShouldBeTrue();
                if (secondaryFailure == "cancel")
                    report.RootElement.GetProperty("CleanupFailurePhases").EnumerateArray()
                        .ShouldContain(phase => phase.GetString()!.StartsWith("read-workers-cancel/", StringComparison.Ordinal));
                report.RootElement.GetProperty("Operations").GetProperty("direct/full/gc-1.iso")
                    .GetProperty("InFlight").GetInt64().ShouldBe(0);
            }
        }
        finally
        {
            await sampling.CancelAsync();
            try
            { await sample; }
            catch (IOException) when (secondaryFailure == "sampler") { }
            await leased.DisposeAsync();
            buffer.Dispose();
            Directory.Delete(root, true);
        }
    }

    private static void EndFixtureLoad(object state) => state.GetType()
        .GetField("_loadStarted", BindingFlags.Instance | BindingFlags.NonPublic)!
        .SetValue(state, Stopwatch.GetTimestamp() - (long)(TimeSpan.FromMinutes(31).TotalSeconds * Stopwatch.Frequency));

    private static Delegate EntryDelegate(Func<object, long, CancellationToken, Task> operation)
    {
        var entryType = typeof(ConvergedReadChecks).GetNestedType("Entry", BindingFlags.NonPublic)!;
        return (Delegate)typeof(ConvergedReadProbeEvidenceTests).GetMethod("CreateEntryDelegate",
            BindingFlags.Static | BindingFlags.NonPublic)!.MakeGenericMethod(entryType).Invoke(null, [operation])!;
    }

    private static Func<TEntry, long, CancellationToken, Task> CreateEntryDelegate<TEntry>(
        Func<object, long, CancellationToken, Task> operation) => (entry, sequence, ct) => operation(entry!, sequence, ct);

    private static Task InvokeWorkerAsync(object state, CancellationTokenSource run, Delegate operation,
        string lane, string operationPrefix, CancellationToken ct) => (Task)typeof(ConvergedReadChecks)
        .GetMethod("WorkerAsync", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null,
            [lane, state.GetType().GetProperty("Entries")!.GetValue(state), state, run, operationPrefix, operation, ct])!;

    private const string ProbeDecoder = "probe-fixture-managed/v1";
    private static RomContentDescriptor MeasuredContent(int romId, RomLogicalIdentity identity, RomContainerFacts container, Sha256 artifact, long artifactBytes)
    {
        var proof = MediaFactFixtures.WholeMeasurement(Guid.NewGuid(), artifact, artifactBytes, identity, container,
            new RomDecoderIdentity.Managed(ProbeDecoder), "complete-output/v1", "probe-fixture-verifier", DateTimeOffset.UnixEpoch, null, null);
        var facts = new RomRepresentationFacts(romId + 10, romId, artifact, artifactBytes, container, RomReconstructionManifest.WholeFile.Instance,
            [new(romId + 100, romId, RomOutputSelector.Whole.Instance, identity)], [proof], []);
        return new(romId, romId + 100, identity, "disc.iso", [new(facts, romId + 100, true)], [], []);
    }
    private static RomDeliveryTarget OriginalTarget(RomContentDescriptor content) => new(content.RomId, content.FileId, RomDeliveryFormat.Original,
        "disc.iso", content.ContentSha256, content.ContentSize, content.ContentSha256, content.ContentSize, content.ArtifactSha256, content.ArtifactSize,
        content.Encoding, content.ContentMd5, content.ContentSha1, content.Encoding == RomRepresentationEncoding.Rvz ? content.Preferred.BindManaged(ProbeDecoder) : null);

    private static object NewState()
    {
        var identity = MediaFactFixtures.Identity(new byte[1024]);
        var hash = identity.Sha256;
        var content = MeasuredContent(1, identity, RomContainerFacts.Identity.Instance, hash, identity.Size);
        var library = new (RomContentDescriptor Content, string IsoPath, string SystemKey)[] { (content, "oracle.iso", "gc") };
        var type = typeof(ConvergedReadChecks).GetNestedType("ReadState", BindingFlags.NonPublic)!;
        object state = Activator.CreateInstance(type, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            null, [TimeSpan.FromMinutes(30), library, ConvergedReadProfile.NintendoWholeFiles], null)!;
        var target = new RomDeliveryTarget(1, 1, RomDeliveryFormat.Original, "disc.iso", hash, 1024,
            hash, 1024, hash, 1024, RomRepresentationEncoding.Identity);
        SetEntry(state, target);
        return state;
    }

    private static object SetEntry(object state, RomDeliveryTarget target)
    {
        var type = typeof(ConvergedReadChecks).GetNestedType("Entry", BindingFlags.NonPublic)!;
        var file = new ConsumerVfsFile("gc-1.iso", target.Sha256, target.SizeBytes, Target: target);
        object entry = Activator.CreateInstance(type, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            null, [target, "oracle.iso", "gc", "gc-1.iso", file], null)!;
        var entries = Array.CreateInstance(type, 1);
        entries.SetValue(entry, 0);
        state.GetType().GetProperty("Entries")!.SetValue(state, entries);
        return entry;
    }

    private static void Record(object state, string name, long bytes, double milliseconds) =>
        state.GetType().GetMethod("Record")!.Invoke(state, [name, bytes, milliseconds]);

    private static JsonDocument Report(object state) =>
        JsonSerializer.SerializeToDocument(state.GetType().GetMethod("Report")!.Invoke(state, null));

    private static IContentAddressableStore NewStoreSpy(IContentAddressableStore inner, object state,
        IHttpContextAccessor accessor)
    {
        var type = typeof(ConvergedReadChecks).GetNestedType("ForwardingStore", BindingFlags.NonPublic);
        type.ShouldNotBeNull("the qualification store must forward the complete port without a growing substitute invocation history");
        return (IContentAddressableStore)Activator.CreateInstance(type!,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, [inner, state, accessor], null)!;
    }

    private static async IAsyncEnumerable<StorageKey> KeysAsync(StorageKey key,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        await Task.CompletedTask;
        yield return key;
    }

    private sealed class AdmissionHandler(byte[] bytes, Sha256 hash) : HttpMessageHandler
    {
        public int Rejected { get; private set; }
        public int Successful { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (request.RequestUri!.AbsolutePath.EndsWith("gc-1.iso", StringComparison.Ordinal))
            {
                Rejected++;
                var rejected = new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { Content = new ByteArrayContent([]) };
                rejected.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromMilliseconds(1));
                return Task.FromResult(rejected);
            }
            Successful++;
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
            response.Headers.ETag = new EntityTagHeaderValue("\"" + hash + "\"");
            return Task.FromResult(response);
        }
    }

    private sealed class FixedResponseHandler(HttpResponseMessage response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(response);
        }
    }

    private sealed class PassThroughAdapter : IRomMediaAdapter
    {
        public RomRepresentationEncoding Encoding => RomRepresentationEncoding.Rvz;
        public bool ReusesIdleImages => true;
        public ValueTask<IRandomAccessContent> OpenAsync(RomArtifactSource source, RomMediaOpenContext context,
            MediaBlockCache cache, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            return source.OpenRandomAccessAsync(ct);
        }
    }

    private sealed class EarlyGrantDecoder : IRomRepresentationDecoder, IAsyncDisposable
    {
        private readonly SemaphoreSlim _slots = new(4);
        private readonly List<ReaderLease> _leases = [];
        private int _attempts, _active;
        public Action ObserveAdmission { get; set; } = () => { };
        public int GrantedCount => _leases.Count;
        public int ActiveCount => _active;
        public int DisposedCount => _leases.Count(lease => lease.Disposed);
        public bool CanDecode(RomRepresentationEncoding format) => format != RomRepresentationEncoding.Chd;
        public async Task<IRandomAccessContent> DecodeAsync(RomRepresentationEncoding format, IRandomAccessContent source,
            CancellationToken ct = default, Sha256? artifact = null, TimeSpan? admissionTimeout = null)
        {
            ObserveAdmission();
            int attempt = ++_attempts;
            try
            {
                // A controlled release lets the final waiter win before the probe inspects its pending state.
                // The preceding deadline and caller-cancellation waits still exercise a full four-slot pool.
                if (attempt == 7)
                    await _leases[0].DisposeAsync();
                if (!await _slots.WaitAsync(admissionTimeout ?? TimeSpan.Zero, ct))
                    throw new ResourceCapacityException("fixture decoder capacity occupied");
                _active++;
                var lease = new ReaderLease(source, this);
                _leases.Add(lease);
                return lease;
            }
            catch { await source.DisposeAsync(); throw; }
        }
        // Random-access fixtures only; any other snapshot fails closed.
        public async Task<IRandomAccessContent> DecodeAsync(RomRepresentationEncoding format, RomArtifactSource source,
            RomMediaOpenContext context, CancellationToken ct = default)
        {
            await using (source)
                return await DecodeAsync(format, await source.OpenRandomAccessAsync(ct), ct, context.Artifact, context.AdmissionTimeout);
        }
        public async ValueTask DisposeAsync()
        {
            foreach (var lease in _leases)
                await lease.DisposeAsync();
            _slots.Dispose();
        }
        private sealed class ReaderLease(IRandomAccessContent source, EarlyGrantDecoder owner) : IRandomAccessContent
        {
            public bool Disposed { get; private set; }
            public long Length => source.Length;
            public ContentReadIntegrity Integrity => source.Integrity;
            public ValueTask<int> ReadAtAsync(long offset, Memory<byte> destination, CancellationToken ct = default) =>
                source.ReadAtAsync(offset, destination, ct);
            public async ValueTask DisposeAsync()
            {
                if (Disposed)
                    return;
                Disposed = true;
                try
                { await source.DisposeAsync(); }
                finally { owner._active--; owner._slots.Release(); }
            }
        }
    }

    private sealed class SaturationMetadataHandler(Sha256 hash, long size, Md5 md5, Sha1 sha1) : HttpMessageHandler
    {
        public string? FirstContentRange { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (request.Method == HttpMethod.Get && !request.Headers.Contains("X-Romd-Probe-Metadata"))
            {
                FirstContentRange ??= request.Headers.Range?.ToString();
                var rejected = new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { Content = new ByteArrayContent([]) };
                rejected.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(1));
                return Task.FromResult(rejected);
            }
            string tag = "\"" + hash + "\"";
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([]) };
            if (request.Method == HttpMethod.Head)
            {
                response.Content.Headers.ContentLength = size;
                response.Headers.ETag = new EntityTagHeaderValue(tag);
                response.Headers.AcceptRanges.Add("bytes");
            }
            else if (request.Method.Method == "PROPFIND")
            {
                response.StatusCode = (HttpStatusCode)207;
                response.Content = new StringContent($"<D:multistatus xmlns:D='DAV:' xmlns:oc='http://owncloud.org/ns'><D:getcontentlength>{size}</D:getcontentlength><D:getetag>{tag}</D:getetag><oc:checksums><oc:checksum>MD5:{md5} SHA1:{sha1}</oc:checksum></oc:checksums></D:multistatus>");
            }
            else if (request.Headers.IfNoneMatch.Count != 0)
            {
                response.StatusCode = HttpStatusCode.NotModified;
                response.Headers.ETag = new EntityTagHeaderValue(tag);
            }
            else if (request.Headers.IfMatch.Count != 0)
                response.StatusCode = HttpStatusCode.PreconditionFailed;
            else if (request.Headers.Range is not null)
            {
                response.StatusCode = HttpStatusCode.RequestedRangeNotSatisfiable;
                response.Content.Headers.ContentRange = new ContentRangeHeaderValue(size);
            }
            else
            {
                response.StatusCode = HttpStatusCode.ServiceUnavailable;
                response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(1));
            }
            return Task.FromResult(response);
        }
    }

    private sealed class FullReadFixture : IAsyncDisposable
    {
        private readonly object _state, _entry;
        private readonly RomRepresentationDecoder _decoder = new();
        private readonly RomContentReader _reader;
        private readonly FullHandler _handler;
        private readonly HttpClient _client;
        private int _directAttempts;
        private long _firstDirectAttemptStarted, _lastDirectAttemptFinished;
        public byte[] Bytes { get; } = Enumerable.Range(0, 513).Select(index => (byte)(index * 37)).ToArray();

        public FullReadFixture()
        {
            _state = NewState();
            var hash = Sha256.FromSpan(SHA256.HashData(Bytes));
            var target = new RomDeliveryTarget(1, 1, RomDeliveryFormat.Original, "disc.iso", hash, Bytes.Length,
                hash, Bytes.Length, hash, Bytes.Length, RomRepresentationEncoding.Identity);
            _entry = SetEntry(_state, target);
            var catalog = Substitute.For<IRomContentCatalog>();
            catalog.FindByRomIdAsync(1, Arg.Any<CancellationToken>()).Returns(
                MeasuredContent(1, MediaFactFixtures.Identity(Bytes), RomContainerFacts.Identity.Instance, hash, Bytes.Length));
            var cas = Substitute.For<IContentAddressableStore>();
            cas.OpenRandomAccessAsync(StorageKey.FromHash(hash), Arg.Any<CancellationToken>())
                .Returns(call => OpenDirectSourceAsync(call.Arg<CancellationToken>()));
            _reader = new RomContentReader(catalog, cas, _decoder);
            _handler = new FullHandler(Bytes, hash);
            _client = new HttpClient(_handler) { BaseAddress = new Uri("http://probe.invalid") };
        }

        public string Operation(bool http) => http ? "client-0/full/gc-1.iso" : "direct/full/gc-1.iso";
        public int Attempts(bool http) => http ? _handler.Attempts : _directAttempts;
        public (long Started, long Finished) AttemptWindow(bool http) => http
            ? _handler.AttemptWindow : (_firstDirectAttemptStarted, _lastDirectAttemptFinished);
        public JsonDocument Report() => ConvergedReadProbeEvidenceTests.Report(_state);
        private async Task<IRandomAccessContent?> OpenDirectSourceAsync(CancellationToken ct)
        {
            int attempt = Interlocked.Increment(ref _directAttempts);
            if (attempt == 1)
                _firstDirectAttemptStarted = Stopwatch.GetTimestamp();
            await Task.Delay(120, ct);
            _lastDirectAttemptFinished = Stopwatch.GetTimestamp();
            if (attempt == 1)
                throw new ResourceCapacityException("fixture capacity occupied");
            return new BytesContent(Bytes);
        }
        public async Task ReadAsync(bool http, CancellationToken ct)
        {
            var method = typeof(ConvergedReadChecks).GetMethod(http ? "FullHttpAsync" : "FullDirectAsync",
                BindingFlags.Static | BindingFlags.NonPublic)!;
            object[] arguments = http ? [_client, _entry, _state, 0, ct] : [_reader, _entry, _state, ct];
            await (Task)method.Invoke(null, arguments)!;
        }
        public async ValueTask DisposeAsync()
        {
            _client.Dispose();
            await _decoder.DisposeAsync();
        }
    }

    private sealed class FullHandler(byte[] bytes, Sha256 hash) : HttpMessageHandler
    {
        public int Attempts { get; private set; }
        private long _firstAttemptStarted, _lastAttemptFinished;
        public (long Started, long Finished) AttemptWindow => (_firstAttemptStarted, _lastAttemptFinished);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Attempts++;
            if (Attempts == 1)
                _firstAttemptStarted = Stopwatch.GetTimestamp();
            await Task.Delay(25, ct);
            _lastAttemptFinished = Stopwatch.GetTimestamp();
            if (Attempts == 1)
            {
                var response = new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { Content = new ByteArrayContent([]) };
                response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromMilliseconds(350));
                return response;
            }
            var success = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
            success.Headers.ETag = new EntityTagHeaderValue("\"" + hash + "\"");
            return success;
        }
    }

    private sealed class BytesContent(byte[] bytes) : IRandomAccessContent
    {
        public int DisposeCount { get; private set; }
        public long Length => bytes.Length;
        public ContentReadIntegrity Integrity => ContentReadIntegrity.None;
        public ValueTask<int> ReadAtAsync(long offset, Memory<byte> destination, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            ArgumentOutOfRangeException.ThrowIfNegative(offset);
            int count = (int)Math.Min(destination.Length, Math.Max(0, bytes.Length - offset));
            if (count != 0)
                bytes.AsMemory((int)offset, count).CopyTo(destination);
            return ValueTask.FromResult(count);
        }
        public ValueTask DisposeAsync() { DisposeCount++; return ValueTask.CompletedTask; }
    }

    private sealed class RangeFixture : IAsyncDisposable
    {
        private readonly string _root;
        private readonly HttpClient _client;
        private readonly object _state, _entry;
        private readonly MethodInfo _ranges;
        private readonly IContentAddressableStore _cas = Substitute.For<IContentAddressableStore>();
        private readonly RomContentReader _reader;
        public RangeHandler Handler { get; }
        public bool NextEntryCompleted { get; private set; }
        public JsonDocument Report() => ConvergedReadProbeEvidenceTests.Report(_state);

        public async Task WorkerAsync(CancellationTokenSource run, CancellationToken ct)
        {
            var entryType = _entry.GetType();
            var target = (RomDeliveryTarget)entryType.GetProperty("Target")!.GetValue(_entry)!;
            var secondTarget = target with { RomId = 2, Filename = "other.iso" };
            var secondFile = new ConsumerVfsFile("wii-2.iso", secondTarget.Sha256, secondTarget.SizeBytes, Target: secondTarget);
            var second = Activator.CreateInstance(entryType, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                null, [secondTarget, (string)entryType.GetProperty("IsoPath")!.GetValue(_entry)!, "wii", "wii-2.iso", secondFile], null)!;
            var entries = Array.CreateInstance(entryType, 2);
            entries.SetValue(_entry, 0);
            entries.SetValue(second, 1);
            _state.GetType().GetProperty("Entries")!.SetValue(_state, entries);
            _state.GetType().GetProperty("Phase")!.SetValue(_state, "sustained-load");
            _state.GetType().GetMethod("BeginLoad")!.Invoke(_state, null);
            var operation = EntryDelegate(async (entry, sequence, token) =>
            {
                await (Task)_ranges.Invoke(null, [_reader, _cas, _client, entry, _state, 0, sequence + 1, token])!;
                if (sequence == 1)
                {
                    NextEntryCompleted = true;
                    // End only this short fixture after both intended attempts; production duration/workload is unchanged.
                    EndFixtureLoad(_state);
                }
            });
            await InvokeWorkerAsync(_state, run, operation, "http-0-original-ranges", "client-0/ranges", ct);
        }

        private RangeFixture(string root, string isoPath, byte[] bytes, RangeHandler handler)
        {
            _root = root;
            Handler = handler;
            _client = new HttpClient(handler) { BaseAddress = new Uri("http://probe.invalid") };
            _reader = new RomContentReader(Substitute.For<IRomContentCatalog>(), _cas,
                Substitute.For<IRomRepresentationDecoder>());
            var hash = Sha256.Parse(Convert.ToHexStringLower(SHA256.HashData(bytes)));
            var artifact = Sha256.Parse(new string('a', 64));
            var content = MeasuredContent(1, MediaFactFixtures.Identity(bytes), MediaFactFixtures.Rvz(bytes.Length, 1, 5, 19, 131072, false), artifact, 128);
            var target = OriginalTarget(content);
            var file = new ConsumerVfsFile("gc-1.iso", hash, bytes.Length, Target: target);
            var library = new (RomContentDescriptor Content, string IsoPath, string SystemKey)[]
            {
                (content, isoPath, "gc")
            };
            var probe = typeof(ConvergedReadChecks);
            var stateType = probe.GetNestedType("ReadState", BindingFlags.NonPublic)!;
            var entryType = probe.GetNestedType("Entry", BindingFlags.NonPublic)!;
            _state = Activator.CreateInstance(stateType, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                null, [TimeSpan.FromMinutes(30), library, ConvergedReadProfile.NintendoWholeFiles], null)!;
            _entry = Activator.CreateInstance(entryType, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                null, [target, isoPath, "gc", "gc-1.iso", file], null)!;
            _ranges = probe.GetMethod("RangesAsync", BindingFlags.Static | BindingFlags.NonPublic)!;
        }

        public long CompletedBatches => (long)_state.GetType().GetMethod("Completed")!
            .Invoke(_state, ["client-0/ranges/gc-1.iso"])!;

        public static async Task<RangeFixture> CreateAsync(bool permanent, TimeSpan retryAfter, CancellationToken ct)
        {
            string root = Directory.CreateTempSubdirectory("romd-read-capacity-").FullName;
            try
            {
                byte[] bytes = new byte[3 * 65536 + 17];
                for (int index = 0; index < bytes.Length; index++)
                    bytes[index] = (byte)(index * 37 + index / 251);
                string iso = Path.Combine(root, "readonly-oracle.iso");
                await File.WriteAllBytesAsync(iso, bytes, ct);
                var hash = Sha256.Parse(Convert.ToHexStringLower(SHA256.HashData(bytes)));
                return new(root, iso, bytes, new(bytes, hash, permanent, retryAfter));
            }
            catch { Directory.Delete(root, true); throw; }
        }

        public async Task ReadAsync(CancellationToken ct)
        {
            var task = (Task)_ranges.Invoke(null, [_reader, _cas, _client, _entry, _state, 0, 1L, ct])!;
            await task;
        }

        public ValueTask DisposeAsync()
        {
            _client.Dispose();
            Directory.Delete(_root, true);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class RangeHandler(byte[] bytes, Sha256 hash, bool permanent, TimeSpan retryAfter) : HttpMessageHandler
    {
        internal const long RejectedOffset = 32767;
        private readonly Dictionary<long, int> _calls = [];
        private readonly HashSet<long> _successful = [];
        public int Rejected { get; private set; }
        public int SuccessfulOffsets => _successful.Count;
        public int CallsAt(long offset) => _calls.GetValueOrDefault(offset);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            long offset = request.Headers.Range!.Ranges.Single().From!.Value;
            _calls[offset] = CallsAt(offset) + 1;
            if (request.RequestUri!.AbsolutePath.EndsWith("gc-1.iso", StringComparison.Ordinal) &&
                offset == RejectedOffset && (permanent || Rejected == 0))
            {
                Rejected++;
                var rejected = new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { Content = new ByteArrayContent([]) };
                rejected.Headers.RetryAfter = new RetryConditionHeaderValue(retryAfter);
                return Task.FromResult(rejected);
            }
            _successful.Add(offset);
            if (offset >= bytes.Length)
            {
                var eof = new HttpResponseMessage(HttpStatusCode.RequestedRangeNotSatisfiable) { Content = new ByteArrayContent([]) };
                eof.Content.Headers.ContentRange = new ContentRangeHeaderValue(bytes.Length);
                return Task.FromResult(eof);
            }
            int wanted = (int)Math.Min(65536, bytes.Length - offset);
            var response = new HttpResponseMessage(HttpStatusCode.PartialContent)
            {
                Content = new ByteArrayContent(bytes, (int)offset, wanted)
            };
            response.Content.Headers.ContentRange = new ContentRangeHeaderValue(offset, offset + wanted - 1, bytes.Length);
            response.Headers.ETag = new EntityTagHeaderValue("\"" + hash + "\"");
            return Task.FromResult(response);
        }
    }
}
