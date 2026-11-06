using System.Reflection;
using System.Text.Json;
using Romd.Application.Common.Content;
using Romd.Domain.Hashing;
using Romd.Domain.Source.Rom;
using Romd.Hosting.Vfs;
using Romd.Infrastructure.Storage;
using Romd.Media;
using Romd.Media.Probe;
using Romd.Storage;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Storage;

// Production VFS instruments are process-scoped. Observe this probe without other collections emitting into its listener.
[CollectionDefinition("Read probe resource observation", DisableParallelization = true)]
public sealed class ReadResourceProbeCollection { }

[Collection("Read probe resource observation")]
public sealed class ReadResourceProbeTests
{
    [Fact]
    public async Task Sample_DefaultDecoder_ObservesActualAdmissionIdleAndCacheWithoutInventingStaging()
    {
        await using var decoder = new RomRepresentationDecoder(new RomMediaAdapterRegistry(new PassThroughAdapter()), new());
        var created = ReadResourceChecks.Create(decoder);
        created.IsError.ShouldBeFalse();
        var resources = created.Value;
        var leases = new List<IRandomAccessContent>();
        try
        {
            for (int index = 0; index < 4; index++)
                leases.Add(await decoder.DecodeAsync(RomRepresentationEncoding.Rvz, new EmptyContent(), CancellationToken.None,
                    Sha256.Parse(new string((char)('a' + index), 64))));

            var occupied = resources.Sample("metadata-and-capacity");

            occupied.IsError.ShouldBeFalse();
            occupied.Value.ActiveDecoderSlots.ShouldBe(4);
            occupied.Value.IdleImages.ShouldBe(0);
            occupied.Value.CacheUsedBytes.ShouldBe(0, "configured cache capacity is not observed cache usage");
            occupied.Value.Staging.ShouldBeNull();
            foreach (var lease in leases)
                await lease.DisposeAsync();
            leases.Clear();
            var settled = resources.Sample("metadata-and-capacity");
            settled.IsError.ShouldBeFalse();
            settled.Value.ActiveDecoderSlots.ShouldBe(0);
            settled.Value.IdleImages.ShouldBe(2);
            using var report = JsonSerializer.SerializeToDocument(resources.Report());
            report.RootElement.GetProperty("StagingScopeProvided").GetBoolean().ShouldBeFalse();
            report.RootElement.GetProperty("ReadPhaseStagingUnchanged").ValueKind.ShouldBe(JsonValueKind.Null);
            report.RootElement.GetProperty("Phases").GetProperty("metadata-and-capacity")
                .GetProperty("SampledPeakChargedStagingBytes").ValueKind.ShouldBe(JsonValueKind.Null);
            report.RootElement.GetProperty("CacheAssemblyMvid").GetGuid().ShouldBe(typeof(MediaBlockCache).Module.ModuleVersionId);
        }
        finally { foreach (var lease in leases) await lease.DisposeAsync(); }
    }

    [Fact]
    public async Task Sample_QueuedAdmission_ObservesActualWaitersAndRetainsLifetimePeak()
    {
        await using var decoder = new RomRepresentationDecoder(new RomMediaAdapterRegistry(new PassThroughAdapter()), new());
        var resources = ReadResourceChecks.Create(decoder).Value;
        var held = new List<IRandomAccessContent>();
        using var cancellation = new CancellationTokenSource();
        Task<IRandomAccessContent>? queued = null;
        try
        {
            for (int index = 0; index < 4; index++)
                held.Add(await decoder.DecodeAsync(RomRepresentationEncoding.Rvz, new EmptyContent()));
            queued = decoder.DecodeAsync(RomRepresentationEncoding.Rvz, new EmptyContent(), cancellation.Token);
            queued.IsCompleted.ShouldBeFalse();
            var waiting = resources.Sample("sustained-load").Value;
            waiting.ActiveDecoderSlots.ShouldBe(4);
            waiting.QueuedDecoderReaders.ShouldBe(1);
            waiting.LifetimePeakQueuedDecoderReaders.ShouldBe(1);
            await cancellation.CancelAsync();
            await Should.ThrowAsync<OperationCanceledException>(() => queued);
            var canceled = resources.Sample("sustained-load").Value;
            canceled.ActiveDecoderSlots.ShouldBe(4);
            canceled.QueuedDecoderReaders.ShouldBe(0);
            canceled.LifetimePeakQueuedDecoderReaders.ShouldBe(1);
        }
        finally
        {
            await cancellation.CancelAsync();
            if (queued is not null)
            {
                try
                { await (await queued).DisposeAsync(); }
                catch (OperationCanceledException) { }
            }
            foreach (var lease in held)
                await lease.DisposeAsync();
        }
        resources.Sample("complete").Value.ActiveDecoderSlots.ShouldBe(0);
    }

    [Theory]
    [InlineData(2, 8, 5000)]
    [InlineData(4, 9, 5000)]
    [InlineData(4, 8, 1000)]
    public async Task Create_NonDefaultDecoder_FailsQualificationInsteadOfReportingDefaults(int active, int queued, int timeout)
    {
        await using var decoder = new RomRepresentationDecoder(new RomMediaAdapterRegistry(), new() { MaxConcurrent = active, MaxQueued = queued, AdmissionTimeoutMilliseconds = timeout });

        var result = ReadResourceChecks.Create(decoder);

        result.IsError.ShouldBeTrue();
        result.FirstError.Code.ShouldBe("ReadProbe.Defaults");
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("duplicate")]
    [InlineData("unknown")]
    [InlineData("oversized")]
    [InlineData("version")]
    public async Task Sample_InvalidAccounting_IsTypedFailureNeverMeasuredZero(string kind)
    {
        using var fixture = new AccountingFixture();
        if (kind != "missing")
        {
            string text = kind switch
            {
                "duplicate" => "{\"Version\":1,\"MaximumFiles\":4,\"MaximumBytes\":4096,\"MaximumFileBytes\":1024,\"Reservations\":{\"0\":32,\"0\":64}}",
                "unknown" => "{\"Version\":1,\"MaximumFiles\":4,\"MaximumBytes\":4096,\"MaximumFileBytes\":1024,\"Wrong\":{}}",
                "oversized" => new string(' ', 32769),
                _ => "{\"Version\":2,\"MaximumFiles\":4,\"MaximumBytes\":4096,\"MaximumFileBytes\":1024,\"Reservations\":{}}"
            };
            await File.WriteAllTextAsync(fixture.Accounting, text, CancellationToken.None);
        }
        await using var decoder = new RomRepresentationDecoder();
        var resources = ReadResourceChecks.Create(decoder, fixture.Scope).Value;

        var result = resources.Sample("sustained-load");

        result.IsError.ShouldBeTrue();
        resources.ReadPhaseStagingUnchanged.ShouldBeFalse();
        using var report = JsonSerializer.SerializeToDocument(resources.Report());
        report.RootElement.GetProperty("Latest").ValueKind.ShouldBe(JsonValueKind.Null);
        report.RootElement.GetProperty("FailureCode").GetString().ShouldNotBeNullOrEmpty();
    }

    [Fact]
    public async Task Sample_ChargedPayloadAndHold_ReportsRealUsageAndDoesNotPromiseEmptyReadStaging()
    {
        using var fixture = new AccountingFixture();
        await fixture.WriteAsync(new Dictionary<int, long> { [0] = 1024 }, CancellationToken.None);
        await File.WriteAllBytesAsync(Path.Combine(fixture.Root, "slot-0.data"), new byte[512], CancellationToken.None);
        await File.WriteAllTextAsync(Path.Combine(fixture.Root, "slot-0.data.quarantine"), "fixture hold", CancellationToken.None);
        await using var decoder = new RomRepresentationDecoder();
        var resources = ReadResourceChecks.Create(decoder, fixture.Scope).Value;

        resources.BeginLoad().IsError.ShouldBeFalse();
        var observed = resources.Snapshot().Staging!;

        observed.ChargedBytes.ShouldBe(1024);
        observed.PayloadBytes.ShouldBe(512);
        observed.Reservations.ShouldBe(1);
        observed.HeldSlots.ShouldBe(1);
        resources.ReadPhaseStagingUnchanged.ShouldBeFalse();
        File.Delete(Path.Combine(fixture.Root, "slot-0.data"));
        File.Delete(Path.Combine(fixture.Root, "slot-0.data.quarantine"));
        await fixture.WriteAsync([], CancellationToken.None);
        resources.Sample("sustained-load").IsError.ShouldBeFalse();
        resources.Snapshot().Staging!.ChargedBytes.ShouldBe(0);
        resources.ReadPhaseStagingUnchanged.ShouldBeFalse("baseline custody cannot become an invented always-empty observation");
    }

    [Fact]
    public async Task Sample_EmptyStableAccounting_ProvesNoNewReadStaging()
    {
        using var fixture = new AccountingFixture();
        await fixture.WriteAsync([], CancellationToken.None);
        await using var decoder = new RomRepresentationDecoder();
        var resources = ReadResourceChecks.Create(decoder, fixture.Scope).Value;

        resources.BeginLoad().IsError.ShouldBeFalse();
        resources.Sample("sustained-load").IsError.ShouldBeFalse();

        resources.ReadPhaseStagingUnchanged.ShouldBeTrue();
        resources.Snapshot().Staging!.Reservations.ShouldBe(0);
    }

    [Fact]
    public async Task Sample_UnchargedPayload_FailsWithoutReclaimingOrDeletingIt()
    {
        using var fixture = new AccountingFixture();
        await fixture.WriteAsync([], CancellationToken.None);
        string payload = Path.Combine(fixture.Root, "slot-0.data");
        await File.WriteAllBytesAsync(payload, new byte[8], CancellationToken.None);
        await using var decoder = new RomRepresentationDecoder();
        var resources = ReadResourceChecks.Create(decoder, fixture.Scope).Value;

        var observed = resources.Sample("sustained-load");

        observed.IsError.ShouldBeTrue();
        observed.FirstError.Code.ShouldBe("ReadProbe.StagingPayload");
        File.Exists(payload).ShouldBeTrue();
        new FileInfo(payload).Length.ShouldBe(8);
    }

    [Fact]
    public async Task Sample_AccountingLeaseContended_IsExplicitlyTransientNotConfirmedZero()
    {
        using var fixture = new AccountingFixture();
        await fixture.WriteAsync([], CancellationToken.None);
        await using var decoder = new RomRepresentationDecoder();
        var resources = ReadResourceChecks.Create(decoder, fixture.Scope).Value;
        using (var held = new FileStream(Path.Combine(fixture.Root, "accounting.lease"), FileMode.Open,
            FileAccess.ReadWrite, FileShare.None))
        {
            var observation = resources.Sample("sustained-load");
            observation.IsError.ShouldBeFalse();
            observation.Value.Staging.ShouldBeNull();
            observation.Value.StagingObservation.ShouldBe("transient-accounting-contention");
            resources.ReadPhaseStagingUnchanged.ShouldBeFalse();
        }
        resources.BeginLoad().IsError.ShouldBeFalse();
        resources.Sample("sustained-load").Value.Staging!.Reservations.ShouldBe(0);
        resources.ReadPhaseStagingUnchanged.ShouldBeTrue();
    }

    [Fact]
    public async Task SampleUntilCancelled_CallerCancels_SamplerJoinsWithoutRetainingSamplesOrLeases()
    {
        await using var decoder = new RomRepresentationDecoder();
        var resources = ReadResourceChecks.Create(decoder).Value;
        using var cancelled = new CancellationTokenSource();
        Task work = resources.SampleUntilCancelledAsync(() => "sustained-load", cancelled.Token);

        await cancelled.CancelAsync();
        await work.WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None);

        resources.Snapshot().ActiveDecoderSlots.ShouldBe(0);
        using var report = JsonSerializer.SerializeToDocument(resources.Report());
        report.RootElement.GetProperty("Samples").GetInt64().ShouldBeGreaterThanOrEqualTo(1);
    }

    [Fact]
    public void ReadBounded_OversizedEvidence_RejectsInsteadOfReadingToEof()
    {
        using var fixture = new AccountingFixture();
        File.WriteAllBytes(fixture.Accounting, new byte[32769]);

        Should.Throw<InvalidDataException>(() => ReadResourceChecks.ReadBounded(fixture.Accounting, 32768));
    }

    [Fact]
    public async Task ControlledQueue_DefaultLeases_ObservesTimeoutCancellationGrantAndMetadataProgress()
    {
        object state = typeof(ConvergedReadProbeEvidenceTests).GetMethod("NewState", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, null)!;
        var probe = typeof(ConvergedReadChecks);
        using var listener = (IDisposable)probe.GetMethod("Listen", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [state])!;
        var admission = new ConsumerVfsAdmission(new ConsumerVfsOptions());
        int metadata = 0;
        Func<CancellationToken, Task> observeMetadata = ct =>
        {
            ct.ThrowIfCancellationRequested();
            admission.QueuedRequestCount.ShouldBeGreaterThan(0);
            metadata++;
            return Task.CompletedTask;
        };
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(40));

        await (Task)probe.GetMethod("ControlledQueueAsync", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, [admission, observeMetadata, state, deadline.Token])!;

        metadata.ShouldBe(2);
        admission.QueuedRequestCount.ShouldBe(0);
        using var report = JsonSerializer.SerializeToDocument(state.GetType().GetMethod("Report")!.Invoke(state, null));
        var observed = report.RootElement.GetProperty("ObservedAdmission");
        observed.GetProperty("ContentActive").GetInt64().ShouldBe(0);
        observed.GetProperty("ContentQueued").GetInt64().ShouldBe(0);
        observed.GetProperty("PeakContentActive").GetInt64().ShouldBe(8);
        observed.GetProperty("PeakContentQueued").GetInt64().ShouldBe(16);
        observed.GetProperty("QueueFullRejections").GetInt64().ShouldBe(1);
        observed.GetProperty("QueueTimeoutRejections").GetInt64().ShouldBe(1);
        report.RootElement.GetProperty("ActualVfsQueueWaitMilliseconds").GetProperty("Count").GetInt64().ShouldBe(19);
    }

    [Fact]
    public async Task ControlledQueue_MetadataCallbackThrows_PreservesFailureAndReleasesEveryWaiterAndLease()
    {
        object state = typeof(ConvergedReadProbeEvidenceTests).GetMethod("NewState", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, null)!;
        var probe = typeof(ConvergedReadChecks);
        using var listener = (IDisposable)probe.GetMethod("Listen", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [state])!;
        var admission = new ConsumerVfsAdmission(new ConsumerVfsOptions());
        var expected = new IOException("metadata callback fixture failure");
        Func<CancellationToken, Task> failMetadata = ct =>
        {
            ct.ThrowIfCancellationRequested();
            admission.QueuedRequestCount.ShouldBe(1, "the callback fails only after a real content waiter exists");
            return Task.FromException(expected);
        };
        Exception? observed = null;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            await (Task)probe.GetMethod("ControlledQueueAsync", BindingFlags.Static | BindingFlags.NonPublic)!
                .Invoke(null, [admission, failMetadata, state, deadline.Token])!;
        }
        catch (Exception error) { observed = error; }
        observed.ShouldBeSameAs(expected, "queue cleanup must preserve the original metadata failure");

        var recovered = new List<IDisposable>();
        OperationCanceledException? blocked = null;
        var users = (Guid[])probe.GetField("Users", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        using var recoveryDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        try
        {
            foreach (Guid user in users)
                for (int index = 0; index < 4; index++)
                {
                    var lease = await admission.EnterAsync(user, null, recoveryDeadline.Token);
                    if (lease is not null)
                        recovered.Add(lease);
                }
        }
        catch (OperationCanceledException error) { blocked = error; }
        finally { foreach (var lease in recovered) lease.Dispose(); }
        state.GetType().GetMethod("ObserveContentAdmission")!.Invoke(state, null);
        using var report = JsonSerializer.SerializeToDocument(state.GetType().GetMethod("Report")!.Invoke(state, null));
        report.RootElement.GetProperty("ObservedAdmission").GetProperty("ContentActive").GetInt64().ShouldBe(0,
            "a callback failure must not orphan a subsequently granted lease");
        admission.QueuedRequestCount.ShouldBe(0);
        blocked.ShouldBeNull("all eight default slots must be independently reusable after failure cleanup");
        recovered.Count.ShouldBe(8);
    }

    [Theory]
    [InlineData("slot-00.data")]
    [InlineData("slot-+0.data")]
    [InlineData("slot-00.lease")]
    [InlineData("directory")]
    [InlineData("canonical-directory")]
    [InlineData("payload-symlink")]
    [InlineData("accounting-directory")]
    [InlineData("reservations-directory")]
    [InlineData("pending-directory")]
    [InlineData("accounting-symlink")]
    [InlineData("reservations-symlink")]
    [InlineData("pending-symlink")]
    public async Task Sample_UnsupportedPoolEntry_RejectsAliasDirectoryAndSymlinkInsteadOfInventingZero(string shape)
    {
        using var fixture = new AccountingFixture();
        await fixture.WriteAsync([], CancellationToken.None);
        if (shape == "directory")
            Directory.CreateDirectory(Path.Combine(fixture.Root, "unrecognized"));
        else if (shape == "canonical-directory")
            Directory.CreateDirectory(Path.Combine(fixture.Root, "slot-0.data"));
        else if (shape == "payload-symlink")
            File.CreateSymbolicLink(Path.Combine(fixture.Root, "slot-0.data"), fixture.Accounting);
        else if (shape.EndsWith("-directory", StringComparison.Ordinal) || shape.EndsWith("-symlink", StringComparison.Ordinal))
        {
            string name = shape.Split('-')[0] switch
            { "accounting" => "accounting.lease", "reservations" => "reservations.json", _ => "reservations.new" };
            string path = Path.Combine(fixture.Root, name);
            File.Delete(path);
            if (shape.EndsWith("-directory", StringComparison.Ordinal))
                Directory.CreateDirectory(path);
            else
                File.CreateSymbolicLink(path, name == "reservations.json" ? Path.Combine(fixture.Root, "accounting.lease") : fixture.Accounting);
        }
        else
            await File.WriteAllBytesAsync(Path.Combine(fixture.Root, shape), new byte[8], CancellationToken.None);
        await using var decoder = new RomRepresentationDecoder();
        var resources = ReadResourceChecks.Create(decoder, fixture.Scope).Value;

        var result = resources.Sample("sustained-load");

        result.IsError.ShouldBeTrue("every pool entry must have its exact canonical name and supported file type");
        result.FirstError.Code.ShouldBe("ReadProbe.StagingFiles", "inventory must reject the unsupported shape before following or counting it");
        resources.ReadPhaseStagingUnchanged.ShouldBeFalse();
        using var report = JsonSerializer.SerializeToDocument(resources.Report());
        report.RootElement.GetProperty("FailureCode").GetString().ShouldNotBeNullOrEmpty();
        report.RootElement.GetProperty("Latest").ValueKind.ShouldBe(JsonValueKind.Null);
        Directory.Exists(Path.Combine(fixture.Root, "unrecognized")).ShouldBe(shape == "directory");
    }

    [Fact]
    public async Task Sample_SustainedAccountingContention_DoesNotCertifyUnchangedAcrossUnknownSamples()
    {
        using var fixture = new AccountingFixture();
        await fixture.WriteAsync([], CancellationToken.None);
        await using var decoder = new RomRepresentationDecoder();
        var resources = ReadResourceChecks.Create(decoder, fixture.Scope).Value;
        resources.BeginLoad().IsError.ShouldBeFalse();
        using (var held = new FileStream(Path.Combine(fixture.Root, "accounting.lease"), FileMode.Open,
            FileAccess.ReadWrite, FileShare.None))
        {
            var unknown = resources.Sample("sustained-load");
            unknown.IsError.ShouldBeFalse();
            unknown.Value.Staging.ShouldBeNull();
            unknown.Value.StagingObservation.ShouldBe("transient-accounting-contention");
        }
        resources.Sample("sustained-load").Value.Staging!.Reservations.ShouldBe(0);

        resources.ReadPhaseStagingUnchanged.ShouldBeFalse("later measured zero cannot fill a gap in sustained observations");
        using var report = JsonSerializer.SerializeToDocument(resources.Report());
        report.RootElement.GetProperty("StagingTransientSamples").GetInt64().ShouldBe(1);
        report.RootElement.GetProperty("SustainedStagingUnknownSamples").GetInt64().ShouldBe(1);
        resources.Sample("complete").IsError.ShouldBeFalse();
        resources.ReadPhaseStagingUnchanged.ShouldBeFalse("a final complete-phase zero does not erase unknown sustained evidence");
        resources.BeginLoad().IsError.ShouldBeFalse();
        resources.ReadPhaseStagingUnchanged.ShouldBeTrue("only a new independently measured baseline starts a new observation period");
    }

    private sealed class AccountingFixture : IDisposable
    {
        public string Root { get; } = Directory.CreateTempSubdirectory("romd-read-resource-").FullName;
        public string Accounting => Path.Combine(Root, "reservations.json");
        public ReadResourceChecks.StagingScope Scope => new(Root, 4, 4096, 1024, 0);
        public AccountingFixture() => File.WriteAllBytes(Path.Combine(Root, "accounting.lease"), []);
        public Task WriteAsync(Dictionary<int, long> reservations, CancellationToken ct) => File.WriteAllTextAsync(Accounting,
            JsonSerializer.Serialize(new
            {
                Version = 1,
                MaximumFiles = 4,
                MaximumBytes = 4096,
                MaximumFileBytes = 1024,
                Reservations = reservations
            }), ct);
        public void Dispose() => Directory.Delete(Root, true);
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
    private sealed class EmptyContent : IRandomAccessContent
    {
        public long Length => 8;
        public ContentReadIntegrity Integrity => ContentReadIntegrity.None;
        public ValueTask<int> ReadAtAsync(long offset, Memory<byte> destination, CancellationToken ct = default)
        { ct.ThrowIfCancellationRequested(); return ValueTask.FromResult(0); }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
