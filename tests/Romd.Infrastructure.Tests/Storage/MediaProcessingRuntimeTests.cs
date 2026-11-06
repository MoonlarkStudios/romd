using System.Diagnostics;
using System.IO.Abstractions;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using NSubstitute;
using Romd.Application.Common.Configuration;
using Romd.Application.Common.Content;
using Romd.Infrastructure.Storage;
using Romd.Media.Processes;
using Romd.Media.TestSupport;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Storage;

public sealed class MediaProcessingRuntimeTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("romd-media-runtime-tests-").FullName;
    private static readonly Guid Job = Guid.Parse("707b8b7b-2e75-4508-aec9-62593346301d");
    private IRomdOptions Options()
    {
        var options = Substitute.For<IRomdOptions>();
        options.DataDirectory.Returns(_root);
        options.MaxUploadBytes.Returns(8L << 20);
        return options;
    }
    private static MediaProcessingOptions Limits(string? worker = null, int concurrent = 1) => new()
    { MaxConcurrent = concurrent, AdmissionTimeoutSeconds = 1, WorkerPath = worker ?? new MediaProcessingOptions().WorkerPath, WallTimeoutSeconds = 30 };
    private string Record => Path.Combine(_root, "temp", "media-runs-v1", "run-0.json");
    private LocalTempFileFactory Files() => new(Options(), new FileSystem(), new TempStorageOptions
    { MaximumBytes = 16L << 20, MaximumFiles = 3, MinimumFreeBytes = 0, AdmissionTimeoutSeconds = 1 });

    private IDisposable HoldAccountingLease() => TryHoldAccountingLease().ShouldNotBeNull();

    private IDisposable? TryHoldAccountingLease()
    {
        string path = Path.Combine(Path.GetDirectoryName(Record)!, "accounting.lease");
        if (!OperatingSystem.IsWindows())
            return NativeFileLease.TryAcquire(path);
        try
        { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException error) when ((error.HResult & 0xffff) is 32 or 33) { return null; }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EnsureJobStoppedAsync_AccountingContention_RechecksCustodyAfterLockRelease(bool launching)
    {
        if (!OperatingSystem.IsWindows() && !NativeLeaseProcessFixture.IsChild)
        {
            using var fixture = new NativeLeaseProcessFixture();
            await fixture.RunAsync(nameof(EnsureJobStoppedAsync_AccountingContention_RechecksCustodyAfterLockRelease), launching.ToString(CultureInfo.InvariantCulture));
            return;
        }
        var runtime = new MediaProcessingRuntime(Options(), Limits());
        using var processing = await runtime.AcquireAsync(Job, "convergence");
        if (launching)
            processing.MarkLaunching();
        string journal = File.ReadAllText(Record);
        try
        {
            using var accounting = HoldAccountingLease();

            var pending = runtime.EnsureJobStoppedAsync(Job);

            pending.IsCompleted.ShouldBeFalse("brief accounting contention must wait before deciding custody");
            File.ReadAllText(Record).ShouldBe(journal);
            accounting.Dispose();
            var result = await pending.WaitAsync(TimeSpan.FromSeconds(10));
            result.IsError.ShouldBe(launching, "acquiring accounting must still require affirmative stopped-child proof");
            if (launching)
                result.FirstError.Description.ShouldBe("The job's media process trees have not been confirmed stopped.");
            File.ReadAllText(Record).ShouldBe(journal);
        }
        finally
        {
            if (launching)
                processing.ConfirmStopped();
        }
    }

    [Fact]
    public async Task EnsureJobStoppedAsync_AccountingRemainsOccupied_TimesOutWithoutChangingCustody()
    {
        if (!OperatingSystem.IsWindows() && !NativeLeaseProcessFixture.IsChild)
        {
            using var fixture = new NativeLeaseProcessFixture();
            await fixture.RunAsync(nameof(EnsureJobStoppedAsync_AccountingRemainsOccupied_TimesOutWithoutChangingCustody));
            return;
        }
        var runtime = new MediaProcessingRuntime(Options(), Limits());
        using var processing = await runtime.AcquireAsync(Job, "convergence");
        string journal = File.ReadAllText(Record);
        using var accounting = HoldAccountingLease();

        var pending = runtime.EnsureJobStoppedAsync(Job);

        pending.IsCompleted.ShouldBeFalse();
        var result = await pending.WaitAsync(TimeSpan.FromSeconds(10));
        result.IsError.ShouldBeTrue();
        result.FirstError.Code.ShouldBe("Rom.Media.Custody");
        result.FirstError.Description.ShouldBe("Media custody accounting is occupied.");
        File.ReadAllText(Record).ShouldBe(journal);
        accounting.Dispose();
        (await runtime.EnsureJobStoppedAsync(Job)).IsError.ShouldBeFalse();
    }

    [Fact]
    public async Task EnsureJobStoppedAsync_CanceledWhileAccountingOccupied_PreservesCancellationAndCustody()
    {
        if (!OperatingSystem.IsWindows() && !NativeLeaseProcessFixture.IsChild)
        {
            using var fixture = new NativeLeaseProcessFixture();
            await fixture.RunAsync(nameof(EnsureJobStoppedAsync_CanceledWhileAccountingOccupied_PreservesCancellationAndCustody));
            return;
        }
        var runtime = new MediaProcessingRuntime(Options(), Limits());
        using var processing = await runtime.AcquireAsync(Job, "convergence");
        string journal = File.ReadAllText(Record);
        using var accounting = HoldAccountingLease();
        using var canceled = new CancellationTokenSource();

        var pending = runtime.EnsureJobStoppedAsync(Job, canceled.Token);
        canceled.Cancel();

        var error = await Should.ThrowAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(10)));
        error.CancellationToken.ShouldBe(canceled.Token);
        File.ReadAllText(Record).ShouldBe(journal);
        accounting.Dispose();
        (await runtime.EnsureJobStoppedAsync(Job)).IsError.ShouldBeFalse();
    }

    [Fact]
    public async Task AcquireAsync_WaitingForOccupiedSlot_ReleasesAccountingWhileBackingOff()
    {
        // The production admission budget keeps the waiter pending through probing on a loaded runner.
        var limits = Limits();
        limits.AdmissionTimeoutSeconds = 5;
        var runtime = new MediaProcessingRuntime(Options(), limits);
        using var owner = await runtime.AcquireAsync(Job, "convergence");
        var waiting = runtime.AcquireAsync(Guid.NewGuid(), "preparation");

        // The owner confirms custody under accounting before it releases the slot the waiter needs. A waiter
        // holding accounting through each 25 ms backoff leaves only brief gaps, which a polling owner can miss
        // for its whole admission budget.
        int probes = 0, available = 0;
        for (var probing = Stopwatch.StartNew(); probing.ElapsedMilliseconds < 300; await Task.Delay(1))
        {
            using var accounting = TryHoldAccountingLease();
            probes++;
            if (accounting is not null)
                available++;
        }

        available.ShouldBeGreaterThan(probes / 2, "a waiting admission must not hold accounting while it backs off");
        waiting.IsCompleted.ShouldBeFalse();
        (await runtime.EnsureJobStoppedAsync(Job)).IsError.ShouldBeFalse();
        owner.Dispose();
        using var admitted = await waiting.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task AcquireAsync_AnotherLiveHost_SharesSingleAdmissionAndNeverTerminatesOwner()
    {
        var runtime = new MediaProcessingRuntime(Options(), Limits());
        using var owner = await runtime.AcquireAsync(Job, "convergence");
        var other = new MediaProcessingRuntime(Options(), Limits());
        await Should.ThrowAsync<ResourceCapacityException>(() => other.AcquireAsync(Guid.NewGuid(), "convergence"));
        MediaProcessIdentity.Read(Environment.ProcessId).ShouldNotBeNull();
        owner.JobId.ShouldBe(Job);
        (await runtime.EnsureJobStoppedAsync(Job)).IsError.ShouldBeFalse("an unstarted owned reservation is affirmative no-child proof");
        owner.Dispose();
        using var sibling = await other.AcquireAsync(Guid.NewGuid(), "convergence");
    }

    [Fact]
    public async Task MarkLaunching_BeforeNativeSpawn_PersistsUnconfirmedLaunchInsteadOfNoChildProof()
    {
        var runtime = new MediaProcessingRuntime(Options(), Limits());
        using var owner = await runtime.AcquireAsync(Job, "convergence");
        try
        {
            owner.MarkLaunching();
            var record = MediaRunRecord.Read(Record, 0, 1).ShouldNotBeNull();
            record.Phase.ShouldBe("launching");
            record.Identity.ShouldBeNull();
            owner.ProcessesStopped.ShouldBeFalse();
        }
        finally { owner.ConfirmStopped(); }
    }

    [Fact]
    public async Task AcquireAsync_LaunchRecordWithoutIdentity_RemainsPinnedWhenOriginalLeaseCanBeAcquired()
    {
        if (!OperatingSystem.IsWindows() && !NativeLeaseProcessFixture.IsChild)
        {
            using var fixture = new NativeLeaseProcessFixture();
            await fixture.RunAsync(nameof(AcquireAsync_LaunchRecordWithoutIdentity_RemainsPinnedWhenOriginalLeaseCanBeAcquired));
            return;
        }
        var runtime = new MediaProcessingRuntime(Options(), Limits());
        using var owner = await runtime.AcquireAsync(Job, "convergence");
        var record = MediaRunRecord.Read(Record, 0, 1).ShouldNotBeNull();
        owner.Dispose();
        MediaRunRecord.Write(Record, record with { Phase = "launching" });
        if (!OperatingSystem.IsWindows())
            using (var independent = NativeFileLease.TryAcquire(Path.Combine(Path.GetDirectoryName(Record)!, "processing-0.lease")))
                independent.ShouldNotBeNull("original OFD closure is not a no-child or reaping certificate");
        (await runtime.EnsureJobStoppedAsync(Job)).IsError.ShouldBeTrue();
        await Should.ThrowAsync<ResourceCapacityException>(() => runtime.AcquireAsync());
        File.Exists(Record).ShouldBeTrue();
    }

    [Fact]
    public async Task EnsureJobStoppedAsync_FreshChildStoppedButParentRetainsPublicationLease_AcceptsNativeProof()
    {
        using var fixture = new MediaWorkerTestFixture();
        var limits = Limits(fixture.Options.WorkerPath);
        var runtime = new MediaProcessingRuntime(Options(), limits);
        using var processing = await runtime.AcquireAsync(Job, "convergence");
        using var active = processing.Activate();
        byte[] bytes = new SyntheticRvzBuilder(0x100000, 0x20000).Build().File;
        await using var aggregate = (await Files().ReserveAggregateAsync(new(Job, "convergence", bytes.Length, 0))).Value;
        processing.TrackAggregate((IMediaStagingAggregate)aggregate);
        var artifact = (await aggregate.MaterializeSourceAsync(new MemoryStream(bytes), Romd.Domain.Hashing.Sha256.FromSpan(System.Security.Cryptography.SHA256.HashData(bytes)), bytes.Length)).Value;
        using var verifier = new RvzVerifier(limits, new MediaWorkerSupervisor(limits, runtime), runtime);
        var result = await verifier.VerifyAsync(artifact);
        result.IsError.ShouldBeFalse(result.IsError ? result.FirstError.Code : string.Empty);
        (await runtime.EnsureJobStoppedAsync(Job)).IsError.ShouldBeFalse();
        File.Exists(Record).ShouldBeTrue("publication still owns admission and its durable stopped journal");
        await Should.ThrowAsync<ResourceCapacityException>(() => new MediaProcessingRuntime(Options(), limits).AcquireAsync());
        result.Value.Verification.CapturedEncodingProfile.ShouldBe(result.Value.EncodingProfile);
    }

    [Theory]
    [InlineData("version")]
    [InlineData("parent")]
    [InlineData("duplicate")]
    [InlineData("extra")]
    [InlineData("oversized")]
    [InlineData("null-binding")]
    [InlineData("null-path")]
    [InlineData("null-pool")]
    public async Task AcquireAsync_UnknownOrMalformedRecord_RemainsPinnedWithoutAgeOrPidSweep(string mutation)
    {
        var runtime = new MediaProcessingRuntime(Options(), Limits());
        using var owner = await runtime.AcquireAsync(Job, "convergence");
        string record = File.ReadAllText(Record);
        owner.Dispose();
        var json = JsonNode.Parse(record)!.AsObject();
        if (mutation == "version")
            json["Version"] = "romd-media-run/v0";
        if (mutation == "parent")
            json.Remove("Parent");
        if (mutation == "extra")
            json["AgeSeconds"] = long.MaxValue;
        if (mutation.StartsWith("null-", StringComparison.Ordinal))
        {
            var binding = new JsonObject
            {
                ["PoolRoot"] = _root,
                ["Slot"] = 0,
                ["Path"] = Path.Combine(_root, "slot-0.data"),
                ["ReservedBytes"] = 1,
                ["JobId"] = Job,
                ["AttemptOperation"] = "convergence",
                ["Role"] = "source"
            };
            if (mutation == "null-path")
                binding["Path"] = null;
            if (mutation == "null-pool")
                binding["PoolRoot"] = null;
            json["Staging"] = mutation == "null-binding" ? new JsonArray((JsonNode?)null) : new JsonArray(binding);
        }
        string changed = mutation == "duplicate" ? record.Insert(1, "\"RunId\":\"" + json["RunId"]!.GetValue<string>() + "\",") : json.ToJsonString();
        if (mutation == "oversized")
            changed = new string('x', MediaRunRecord.MaximumBytes + 1);
        File.WriteAllText(Record, changed);
        await Should.ThrowAsync<ResourceCapacityException>(() => runtime.AcquireAsync());
        (await runtime.EnsureJobStoppedAsync(Job)).IsError.ShouldBeTrue();
        File.ReadAllText(Record).ShouldBe(changed);
    }

    [Fact]
    public async Task AcquireAsync_DifferentCapacityWithActiveRun_FailsClosedInsteadOfAddingSlots()
    {
        var runtime = new MediaProcessingRuntime(Options(), Limits());
        using var owner = await runtime.AcquireAsync(Job, "convergence");
        await Should.ThrowAsync<InvalidDataException>(() => new MediaProcessingRuntime(Options(), Limits(concurrent: 2)).AcquireAsync());
        Directory.GetFiles(Path.GetDirectoryName(Record)!, "run-*.json").Length.ShouldBe(1);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AcquireAsync_ParentIdentityUnreadable_DoesNotSignalItsKnownLiveChildOrReleaseCharges(bool foreignNamespace)
    {
        using var child = new MediaWorkerTestFixture(inspectionMode: "descendant-hang");
        using var parentFixture = new MediaRuntimeParentFixture();
        using var adoption = new MediaWorkerProcessLifetime();
        byte[] bytes = new SyntheticRvzBuilder(0x100000, 0x20000).Build().File;
        string source = Path.Combine(_root, "original");
        File.WriteAllBytes(source, bytes);
        using var parent = parentFixture.Start(_root, child.Options.WorkerPath, source, "permitted", false);
        Task<string> stdout = parent.StandardOutput.ReadToEndAsync();
        Task<string> stderr = parent.StandardError.ReadToEndAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            while (!File.Exists(Path.Combine(_root, "ready")))
            {
                if (parent.HasExited)
                    throw new InvalidOperationException($"Parent fixture exited {parent.ExitCode}: {await stdout}; {await stderr}");
                await Task.Delay(10, deadline.Token);
            }
            var record = MediaRunRecord.Read(Record, 0, 1).ShouldNotBeNull();
            var identity = record.Identity.ShouldNotBeNull();
            string journal = File.ReadAllText(Record);
            string charges = File.ReadAllText(Path.Combine(_root, "temp", "staging-v1", "reservations.json"));
            var uncertain = new MediaProcessingRuntime(Path.GetDirectoryName(Record)!, Limits(child.Options.WorkerPath),
                pid => pid == parent.Id ? foreignNamespace ? record.Parent with { PidNamespace = "pid:[1]" } : null : MediaProcessIdentity.Read(pid));

            await Should.ThrowAsync<ResourceCapacityException>(() => uncertain.AcquireAsync(Guid.NewGuid(), "convergence", deadline.Token));

            MediaWorkerTestFixture.IsRunning(identity.ProcessId).ShouldBeTrue("an unreadable or foreign parent is not proof of orphaning");
            parent.HasExited.ShouldBeFalse();
            File.ReadAllText(Record).ShouldBe(journal);
            File.ReadAllText(Path.Combine(_root, "temp", "staging-v1", "reservations.json")).ShouldBe(charges);
        }
        finally
        {
            if (!parent.HasExited)
            { parent.Kill(entireProcessTree: false); await parent.WaitForExitAsync(CancellationToken.None); }
            await Task.WhenAll(stdout, stderr);
            using var recovered = await new MediaProcessingRuntime(Options(), Limits(child.Options.WorkerPath)).AcquireAsync(ct: deadline.Token);
        }
    }

    [Theory]
    [InlineData("prepared", false)]
    [InlineData("spawned", false)]
    [InlineData("identified", false)]
    [InlineData("permitted", false)]
    [InlineData("permitted", true)]
    [InlineData("verified", false)]
    [InlineData("verified", true)]
    public async Task ParentKilled_Checkpoint_StopsOwnedTreeRecoversExactChargesAndRunsSibling(string checkpoint, bool encode)
    {
        string mode = checkpoint == "permitted" ? encode ? "encoded-hang" : "descendant-hang" : "valid";
        using var child = new MediaWorkerTestFixture(inspectionMode: mode);
        using var parentFixture = new MediaRuntimeParentFixture();
        using var adoption = new MediaWorkerProcessLifetime();
        byte[] bytes = encode ? SyntheticNintendoIso.GameCube() : new SyntheticRvzBuilder(0x100000, 0x20000).Build().File;
        string source = Path.Combine(_root, "original");
        File.WriteAllBytes(source, bytes);
        using var parent = parentFixture.Start(_root, child.Options.WorkerPath, source, checkpoint, encode);
        Task<string> stdout = parent.StandardOutput.ReadToEndAsync();
        Task<string> stderr = parent.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            while (!File.Exists(Path.Combine(_root, "ready")))
            {
                if (parent.HasExited)
                    throw new InvalidOperationException($"Parent fixture exited {parent.ExitCode}: {await stdout}; {await stderr}");
                await Task.Delay(10, timeout.Token);
            }
            string stagedPath = File.ReadAllText(Path.Combine(_root, "source-path"));
            string? outputPath = encode ? File.ReadAllText(Path.Combine(_root, "output-path")) : null;
            MediaProcessIdentity? identity = null;
            var before = MediaRunRecord.Read(Record, 0, 1).ShouldNotBeNull();
            if (checkpoint != "prepared")
                identity = before.Identity ?? MediaProcessingRuntime.ReadIdentity(Record, before.RunId, before.Admission);
            if (checkpoint is "spawned" or "identified")
                File.Exists(stagedPath + ".pid").ShouldBeFalse("no payload is allowed before permit");
            if (encode && checkpoint == "permitted")
                new FileInfo(outputPath!).Length.ShouldBeGreaterThan(0L);
            int? descendant = File.Exists(stagedPath + ".descendant.pid") ? int.Parse(File.ReadAllText(stagedPath + ".descendant.pid")) : null;
            parent.Kill(entireProcessTree: false);
            await parent.WaitForExitAsync(timeout.Token);
            await Task.WhenAll(stdout, stderr);

            using var healthy = new MediaWorkerTestFixture();
            var limits = Limits(healthy.Options.WorkerPath);
            var restarted = new MediaProcessingRuntime(Options(), limits);
            using var next = await restarted.AcquireAsync(Guid.NewGuid(), "convergence", timeout.Token);
            if (identity is not null)
                MediaWorkerTestFixture.IsRunning(identity.ProcessId).ShouldBeFalse();
            if (descendant is { } pid)
                MediaWorkerTestFixture.IsRunning(pid).ShouldBeFalse();
            File.Exists(stagedPath + ".quarantine").ShouldBeFalse();
            if (outputPath is not null)
                File.Exists(outputPath + ".quarantine").ShouldBeFalse();
            var stopped = await restarted.EnsureJobStoppedAsync(Job, timeout.Token);
            stopped.IsError.ShouldBeFalse(stopped.IsError
                ? $"{stopped.FirstError.Code}: {stopped.FirstError.Description}"
                : string.Empty);
            using var active = next.Activate();
            await using var sibling = await Files().CreateAsync(new MemoryStream(new SyntheticRvzBuilder(0x100000, 0x20000).Build().File));
            using var verifier = new RvzVerifier(limits, new MediaWorkerSupervisor(limits, restarted), restarted);
            var result = await verifier.VerifyAsync(sibling, timeout.Token);
            result.IsError.ShouldBeFalse(result.IsError ? result.FirstError.Code : string.Empty);
            File.ReadAllBytes(source).ShouldBe(bytes);
        }
        finally
        {
            if (!parent.HasExited)
            { parent.Kill(entireProcessTree: false); await parent.WaitForExitAsync(CancellationToken.None); }
            await Task.WhenAll(stdout, stderr);
        }
    }

    [Theory]
    [InlineData("matching")]
    [InlineData("live")]
    [InlineData("birth")]
    [InlineData("group")]
    [InlineData("unknown")]
    public async Task EnsureJobStoppedAsync_IndependentlyAcquiredLease_ReapsOnlyMatchedAdoptedZombie(string condition)
    {
        if (!OperatingSystem.IsLinux())
            return;
        if (!NativeLeaseProcessFixture.IsChild)
        {
            using var fixture = new NativeLeaseProcessFixture();
            await fixture.RunAsync(nameof(EnsureJobStoppedAsync_IndependentlyAcquiredLease_ReapsOnlyMatchedAdoptedZombie), condition);
            return;
        }
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var adoption = new MediaWorkerProcessLifetime();
        await using var child = await UnreapedChild.StartAsync(deadline.Token);
        var identity = child.Identity;
        if (condition != "live")
            await child.StopAsync(deadline.Token);
        MediaWorkerProcessLifetime.IsAdoptedZombie(identity).ShouldBe(condition != "live");
        var runtime = new MediaProcessingRuntime(Options(), Limits());
        using var owner = await runtime.AcquireAsync(Job, "convergence", deadline.Token);
        byte[] bytes = [1, 2, 3, 4];
        await using var aggregate = (await Files().ReserveAggregateAsync(new(Job, "convergence", bytes.Length, 0), deadline.Token)).Value;
        var artifact = (await aggregate.MaterializeSourceAsync(new MemoryStream(bytes),
            Romd.Domain.Hashing.Sha256.FromSpan(System.Security.Cryptography.SHA256.HashData(bytes)), bytes.Length, deadline.Token)).Value;
        owner.TrackAggregate((IMediaStagingAggregate)aggregate);
        var record = MediaRunRecord.Read(Record, 0, 1).ShouldNotBeNull();
        owner.Dispose();
        var recordedChild = condition switch
        {
            "birth" => identity with { Birth = (ulong.Parse(identity.Birth, CultureInfo.InvariantCulture) + 1).ToString(CultureInfo.InvariantCulture) },
            "group" => identity with { GroupId = identity.GroupId + 1 },
            _ => identity
        };
        MediaWorkerProcessLifetime.IsAdoptedZombie(recordedChild).ShouldBe(condition is "matching" or "unknown");
        MediaRunRecord.Write(Record, record with { Phase = "running", Identity = recordedChild });
        using (var independent = NativeFileLease.TryAcquire(Path.Combine(Path.GetDirectoryName(Record)!, "processing-0.lease")))
            independent.ShouldNotBeNull().Identity.ShouldBe(record.Admission);
        string journal = File.ReadAllText(Record);
        string ledgerPath = Path.Combine(_root, "temp", "staging-v1", "reservations.json");
        string charges = File.ReadAllText(ledgerPath);
        if (condition == "unknown")
            runtime = new MediaProcessingRuntime(Path.GetDirectoryName(Record)!, Limits(),
            pid => pid == identity.ProcessId ? null : MediaProcessIdentity.Read(pid));

        var result = await runtime.EnsureJobStoppedAsync(Job, deadline.Token);

        result.IsError.ShouldBe(condition != "matching");
        if (condition == "matching")
        {
            File.Exists(Record).ShouldBeFalse();
            MediaProcessIdentity.Observe(identity.ProcessId).Presence.ShouldBe(MediaProcessPresence.Gone);
        }
        else
        {
            File.ReadAllText(Record).ShouldBe(journal);
            File.ReadAllText(ledgerPath).ShouldBe(charges);
            MediaProcessIdentity.Read(identity.ProcessId).ShouldBe(identity, "rejected custody must not reap or signal the actual child");
            await Should.ThrowAsync<ResourceCapacityException>(() => runtime.AcquireAsync(ct: deadline.Token));
            File.ReadAllText(Record).ShouldBe(journal);
            File.ReadAllText(ledgerPath).ShouldBe(charges);
            File.Delete(Record); // Test-owned synthetic journal; the actual fixture still owns and joins its child.
        }
        File.ReadAllBytes(artifact.Path).ShouldBe(bytes);
    }

    [Fact]
    public async Task IsAdoptedZombie_ChildOfStoppedFixtureParent_RejectsUntilActuallyAdopted()
    {
        if (!OperatingSystem.IsLinux())
            return;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var adoption = new MediaWorkerProcessLifetime();
        await using var family = await UnreapedFamily.StartAsync(_root, deadline.Token);
        var parent = family.Parent;
        var child = family.Child;
        int childPid = child.ProcessId;
        UnreapedChild.Stat(childPid)[1].ShouldBe(parent.Identity.ProcessId.ToString(CultureInfo.InvariantCulture));
        UnreapedChild.Signal(child, 9);
        while (UnreapedChild.Stat(childPid)[0] != "Z")
            await Task.Delay(10, deadline.Token);
        MediaWorkerProcessLifetime.IsAdoptedZombie(child).ShouldBeFalse();
        MediaProcessIdentity.Read(childPid).ShouldBe(child);
        var runtime = new MediaProcessingRuntime(Options(), Limits());
        using var owner = await runtime.AcquireAsync(Job, "convergence", deadline.Token);
        await using var aggregate = (await Files().ReserveAggregateAsync(new(Job, "convergence", 4, 0), deadline.Token)).Value;
        owner.TrackAggregate((IMediaStagingAggregate)aggregate);
        var record = MediaRunRecord.Read(Record, 0, 1).ShouldNotBeNull();
        owner.Dispose();
        MediaRunRecord.Write(Record, record with { Phase = "running", Identity = child });
        string journal = File.ReadAllText(Record);
        string ledger = Path.Combine(_root, "temp", "staging-v1", "reservations.json");
        string charges = File.ReadAllText(ledger);
        (await runtime.EnsureJobStoppedAsync(Job, deadline.Token)).IsError.ShouldBeTrue();
        File.ReadAllText(Record).ShouldBe(journal);
        File.ReadAllText(ledger).ShouldBe(charges);
        await parent.DisposeAsync();
        while (UnreapedChild.Stat(childPid)[1] != Environment.ProcessId.ToString(CultureInfo.InvariantCulture))
            await Task.Delay(10, deadline.Token);
        MediaWorkerProcessLifetime.IsAdoptedZombie(child).ShouldBeTrue();
        (await runtime.EnsureJobStoppedAsync(Job, deadline.Token)).IsError.ShouldBeFalse();
        MediaProcessIdentity.Observe(childPid).Presence.ShouldBe(MediaProcessPresence.Gone);
        File.Exists(Record).ShouldBeFalse();
    }

    [Theory]
    [InlineData("published")]
    [InlineData("captured")]
    [InlineData("released")]
    [InlineData("transitioned")]
    public async Task UnreapedFamily_SetupCanceled_StopsAndReapsOwnedDescendants(string checkpoint)
    {
        if (!OperatingSystem.IsLinux())
            return;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var adoption = new MediaWorkerProcessLifetime();
        int childPid = 0;
        await Should.ThrowAsync<OperationCanceledException>(() => UnreapedFamily.StartAsync(_root, deadline.Token, stage =>
        {
            if (stage != checkpoint)
                return;
            childPid = int.Parse(File.ReadAllText(Path.Combine(_root, "unadopted-child")), CultureInfo.InvariantCulture);
            deadline.Cancel();
        }));
        childPid.ShouldBeGreaterThan(1);
        MediaProcessIdentity.Observe(childPid).Presence.ShouldBe(MediaProcessPresence.Gone);
    }

    [Fact]
    public async Task UnreapedChild_StartAsync_ClosesUnrelatedInheritableLease()
    {
        if (!OperatingSystem.IsLinux())
            return;
        if (!NativeLeaseProcessFixture.IsChild)
        {
            using var fixture = new NativeLeaseProcessFixture();
            await fixture.RunAsync(nameof(UnreapedChild_StartAsync_ClosesUnrelatedInheritableLease));
            return;
        }
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var adoption = new MediaWorkerProcessLifetime();
        string leasePath = Path.Combine(_root, "unrelated.lease");
        using var original = NativeFileLease.TryAcquire(leasePath).ShouldNotBeNull();
        UnreapedChild? child = null;
        try
        {
            // Model the open-before-CLOEXEC window using only this test's owned descriptor.
            NativeFileLease.Fcntl(original.Descriptor, 2, 0).ShouldBe(0);
            child = await UnreapedChild.StartAsync(deadline.Token);
            NativeFileLease.Fcntl(original.Descriptor, 2, 1).ShouldBe(0);
            Directory.EnumerateFiles($"/proc/{child.Identity.ProcessId}/fd")
                .Any(path => new FileInfo(path).LinkTarget == leasePath).ShouldBeFalse();
            original.Dispose();
            using var independent = NativeFileLease.TryAcquire(leasePath);
            independent.ShouldNotBeNull("the fixture must not retain another run's OFD admission");
        }
        finally
        {
            if (!original.IsClosed)
                NativeFileLease.Fcntl(original.Descriptor, 2, 1).ShouldBe(0);
            original.Dispose();
            if (child is not null)
                await child.DisposeAsync();
        }
    }

    // The descendant cannot leave its parent's owned group until its exact identity is retained.
    private sealed class UnreapedFamily(UnreapedChild parent) : IAsyncDisposable
    {
        private MediaProcessIdentity? _child;
        public UnreapedChild Parent { get; } = parent;
        public MediaProcessIdentity Child => _child.ShouldNotBeNull();
        public static async Task<UnreapedFamily> StartAsync(string root, CancellationToken ct, Action<string>? checkpoint = null)
        {
            string childPath = Path.Combine(root, "unadopted-child"), gatePath = Path.Combine(root, "child-gate");
            const string waitForGate = "while [ ! -e \"$1\" ]; do /bin/sleep 0.01; done; exec /usr/bin/setsid /bin/sleep 300";
            var parent = await UnreapedChild.StartAsync(ct, "/bin/sh", "-c",
                "/bin/sh -c \"$3\" fixture \"$2\" & echo $! > \"$1\"; kill -STOP $$; wait",
                "fixture", childPath, gatePath, waitForGate);
            var family = new UnreapedFamily(parent);
            try
            {
                while (!File.Exists(childPath) || string.IsNullOrWhiteSpace(File.ReadAllText(childPath)))
                    await Task.Delay(10, ct);
                while (UnreapedChild.Stat(parent.Identity.ProcessId)[0] != "T")
                    await Task.Delay(10, ct);
                At("published");
                int childPid = int.Parse(File.ReadAllText(childPath), CultureInfo.InvariantCulture);
                family._child = MediaProcessIdentity.Read(childPid).ShouldNotBeNull();
                family._child.GroupId.ShouldBe(parent.Identity.GroupId);
                UnreapedChild.Stat(childPid)[1].ShouldBe(parent.Identity.ProcessId.ToString(CultureInfo.InvariantCulture));
                At("captured");
                File.WriteAllText(gatePath, "go");
                At("released");
                var leader = await UnreapedChild.ObserveLeaderAsync(childPid, ct);
                At("transitioned");
                leader.ShouldBe(family._child with { GroupId = childPid });
                family._child = leader;
                return family;
            }
            catch
            {
                await family.DisposeAsync();
                throw;
            }
            void At(string stage)
            { checkpoint?.Invoke(stage); ct.ThrowIfCancellationRequested(); }
        }
        public async ValueTask DisposeAsync()
        {
            try
            { await Parent.DisposeAsync(); }
            finally
            {
                if (_child is { } expected && MediaProcessIdentity.Observe(expected.ProcessId).Presence != MediaProcessPresence.Gone)
                {
                    var current = MediaProcessIdentity.Read(expected.ProcessId).ShouldNotBeNull();
                    current.ShouldBe(expected with { GroupId = current.GroupId });
                    (current.GroupId == Parent.Identity.GroupId || current.GroupId == current.ProcessId).ShouldBeTrue();
                    await UnreapedChild.CleanupAsync(current);
                }
            }
        }
    }

    // Native spawn deliberately leaves wait status with this fixture, allowing actual Z-state assertions.
    // No Process wait owner or background reaper can consume the child before the runtime under test.
    private sealed class UnreapedChild(MediaProcessIdentity identity) : IAsyncDisposable
    {
        private bool _disposed;
        public MediaProcessIdentity Identity { get; } = identity;
        public static async Task<UnreapedChild> StartAsync(CancellationToken ct, params string[] command)
        {
            string[] args = ["/usr/bin/setsid", .. (command.Length == 0 ? new[] { "/bin/sleep", "300" } : command)];
            nint argv = Marshal.AllocHGlobal((args.Length + 1) * nint.Size);
            nint environment = Marshal.AllocHGlobal(nint.Size);
            var strings = new List<nint>();
            int pid = 0;
            bool spawned = false, actionsInitialized = false;
            nint actions = Marshal.AllocHGlobal(1024);
            try
            {
                for (int index = 0; index < args.Length; index++)
                {
                    nint value = Marshal.StringToCoTaskMemUTF8(args[index]);
                    strings.Add(value);
                    Marshal.WriteIntPtr(argv, index * nint.Size, value);
                }
                Marshal.WriteIntPtr(argv, args.Length * nint.Size, 0);
                Marshal.WriteIntPtr(environment, 0);
                Marshal.Copy(new byte[1024], 0, actions, 1024);
                ActionsInit(actions).ShouldBe(0);
                actionsInitialized = true;
                CloseFrom(actions, 3).ShouldBe(0);
                int result = Spawn(out pid, args[0], actions, 0, argv, environment);
                spawned = result == 0;
                result.ShouldBe(0);
                pid.ShouldBeGreaterThan(1);
                return new(await ObserveLeaderAsync(pid, ct));
            }
            catch
            {
                // The returned spawn PID is our unreaped direct child, never a guessed PID or process group.
                if (spawned && pid > 1 && MediaProcessIdentity.Read(pid) is { } started)
                    await CleanupAsync(started);
                throw;
            }
            finally
            {
                if (actionsInitialized)
                    ActionsDestroy(actions);
                Marshal.FreeHGlobal(actions);
                foreach (nint value in strings)
                    Marshal.FreeCoTaskMem(value);
                Marshal.FreeHGlobal(argv);
                Marshal.FreeHGlobal(environment);
            }
        }
        public async Task StopAsync(CancellationToken ct)
        {
            Signal(Identity, 9);
            while (Stat(Identity.ProcessId)[0] != "Z")
                await Task.Delay(10, ct);
        }
        public static async Task<MediaProcessIdentity> ObserveLeaderAsync(int pid, CancellationToken ct)
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                if (MediaProcessIdentity.Read(pid) is { } observed && observed.ProcessId > 1 && observed.GroupId == observed.ProcessId)
                    return observed;
                await Task.Delay(10, ct);
            }
        }
        public static string[] Stat(int pid)
        {
            string stat = File.ReadAllText($"/proc/{pid}/stat");
            return stat[(stat.LastIndexOf(')') + 2)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        }
        public static void Signal(MediaProcessIdentity expected, int signal)
        {
            expected.ProcessId.ShouldBeGreaterThan(1);
            MediaProcessIdentity.Read(expected.ProcessId).ShouldBe(expected, "fixture signals only its exact known child identity");
            Kill(expected.ProcessId, signal).ShouldBe(0);
        }
        public static async Task CleanupAsync(MediaProcessIdentity expected)
        {
            if (MediaProcessIdentity.Observe(expected.ProcessId).Presence == MediaProcessPresence.Gone)
                return;
            expected.ProcessId.ShouldBeGreaterThan(1);
            MediaProcessIdentity.Read(expected.ProcessId).ShouldBe(expected);
            bool ownsGroup = expected.GroupId == expected.ProcessId;
            Kill(ownsGroup ? -expected.GroupId : expected.ProcessId, 9).ShouldBe(0);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (true)
            {
                int reaped = WaitPid(expected.ProcessId, out _, 1);
                if (reaped == expected.ProcessId || reaped == -1 && Marshal.GetLastPInvokeError() == 10)
                    break;
                await Task.Delay(10, deadline.Token);
            }
            if (ownsGroup)
            {
                using var lifetime = new MediaWorkerProcessLifetime();
                await lifetime.JoinDescendantsAsync(expected.GroupId, deadline.Token);
            }
        }
        public async ValueTask DisposeAsync()
        {
            if (_disposed)
                return;
            _disposed = true;
            await CleanupAsync(Identity);
        }
        [DllImport("libc", EntryPoint = "posix_spawn_file_actions_init")] private static extern int ActionsInit(nint actions);
        [DllImport("libc", EntryPoint = "posix_spawn_file_actions_destroy")] private static extern int ActionsDestroy(nint actions);
        [DllImport("libc", EntryPoint = "posix_spawn_file_actions_addclosefrom_np")] private static extern int CloseFrom(nint actions, int first);
        [DllImport("libc", EntryPoint = "posix_spawn")]
        private static extern int Spawn(out int pid,
            [MarshalAs(UnmanagedType.LPUTF8Str)] string path, nint actions, nint attributes, nint argv, nint environment);
        [DllImport("libc", EntryPoint = "kill", SetLastError = true)] private static extern int Kill(int pid, int signal);
        [DllImport("libc", EntryPoint = "waitpid", SetLastError = true)] private static extern int WaitPid(int pid, out int status, int options);
    }

    public void Dispose()
    {
        if (Directory.EnumerateFiles(_root, "*.quarantine", SearchOption.AllDirectories).Any() ||
            Directory.EnumerateFiles(_root, "run-*.json*", SearchOption.AllDirectories).Any())
            return;
        Directory.Delete(_root, recursive: true);
    }
}
