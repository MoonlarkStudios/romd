using System.IO.Abstractions;
using System.Security.Cryptography;
using System.Text.Json;
using NSubstitute;
using Romd.Application.Common.Configuration;
using Romd.Infrastructure.Storage;
using Romd.Media.Inspection;
using Romd.Media.Probe;
using Romd.Media.TestSupport;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Storage;

public sealed class DefaultProcessingProbeTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("romd-default-processing-probe-").FullName;
    private readonly MediaProcessingOptions _limits = new();

    private (MediaProcessingRuntime Runtime, LocalTempFileFactory Staging, string Iso, byte[] Bytes) Setup()
    {
        var options = Substitute.For<IRomdOptions>();
        options.DataDirectory.Returns(_root);
        options.MaxUploadBytes.Returns(10L << 30);
        var bytes = SyntheticNintendoIso.GameCube();
        string iso = Path.Combine(_root, "recovery.iso");
        File.WriteAllBytes(iso, bytes);
        return (new(options, _limits), new(options, new FileSystem()), iso, bytes);
    }

    [Fact]
    public async Task RunAsync_DefaultTwoChildren_DefersThirdAndReturnsSeparateProofsWithoutLeases()
    {
        var fixture = Setup();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var evidence = await DefaultProcessingChecks.RunAsync(fixture.Iso, _limits, fixture.Runtime, fixture.Staging, deadline.Token);

        evidence.IdentifiedChildren.Count.ShouldBe(2);
        evidence.IdentifiedChildren.Select(identity => identity.ProcessId).Distinct().Count().ShouldBe(2);
        evidence.ThirdAdmissionSeconds.ShouldBeGreaterThanOrEqualTo(5);
        evidence.RecoveredSlots.ShouldBe(2);
        evidence.Scope.ShouldContain("pre-permit");
        string sha = Convert.ToHexStringLower(SHA256.HashData(fixture.Bytes));
        foreach (var lane in evidence.Lanes)
        {
            lane.Verify.Reply!.Inspection!.LogicalSha256.ShouldBe(sha);
            lane.Verify.Reply.Inspection.LogicalBytes.ShouldBe(fixture.Bytes.Length);
            lane.Verify.Execution!.ProcessId.ShouldNotBe(lane.Encode.Execution!.ProcessId);
            File.Exists(lane.SourcePath).ShouldBeFalse("the helper owns and disposes both distinct source leases");
            DefaultProcessingChecks.AssertIdentityStopped(lane.Identified);
        }
        AssertStagingEmpty();
        await AssertAllSlotsReusableAsync(fixture.Runtime, deadline.Token);
    }

    [Fact]
    public async Task RunAsync_OversizedRecovery_RejectsBeforeStagingOrChildAdmission()
    {
        var fixture = Setup();
        await using (var oversized = new FileStream(fixture.Iso, FileMode.Open, FileAccess.Write, FileShare.None))
            oversized.SetLength(9L << 20);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        var error = await Should.ThrowAsync<InvalidOperationException>(() => DefaultProcessingChecks.RunAsync(
            fixture.Iso, _limits, fixture.Runtime, fixture.Staging, deadline.Token));

        error.Message.ShouldContain("bounded tiny recovery fixture");
        Directory.GetFiles(_root, "*.data", SearchOption.AllDirectories).ShouldBeEmpty();
        File.Exists(Path.Combine(_root, "temp", "staging-v1", "reservations.json")).ShouldBeFalse();
        await AssertAllSlotsReusableAsync(fixture.Runtime, deadline.Token);
    }

    [Fact]
    public async Task RunAsync_ObserverFailsWithTwoLiveChildren_PreservesFailureAndJoinsEveryLease()
    {
        var fixture = Setup();
        var primary = new IOException("injected observation failure");
        MediaProcessIdentity[] children = [];
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        Exception? observed = null;
        try
        {
            await DefaultProcessingChecks.RunAsync(fixture.Iso, _limits, fixture.Runtime, fixture.Staging, deadline.Token,
                (point, identified, token) =>
                {
                    point.ShouldBe(DefaultProcessingChecks.Point.BothIdentified);
                    token.ShouldBe(deadline.Token);
                    children = identified.ToArray();
                    return Task.FromException(primary);
                });
        }
        catch (Exception error) { observed = error; }

        observed.ShouldBeSameAs(primary);
        children.Length.ShouldBe(2);
        foreach (var child in children)
            DefaultProcessingChecks.AssertIdentityStopped(child);
        AssertStagingEmpty();
        await AssertAllSlotsReusableAsync(fixture.Runtime, deadline.Token);
    }

    [Fact]
    public async Task RunAsync_CallerCancelsAtIdentifiedBarrier_PreservesTokenAndJoinsBothChildren()
    {
        var fixture = Setup();
        MediaProcessIdentity[] children = [];
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        OperationCanceledException? observed = null;
        try
        {
            await DefaultProcessingChecks.RunAsync(fixture.Iso, _limits, fixture.Runtime, fixture.Staging, cancellation.Token,
                async (_, identified, token) =>
                {
                    token.ShouldBe(cancellation.Token);
                    children = identified.ToArray();
                    await cancellation.CancelAsync();
                });
        }
        catch (OperationCanceledException error) { observed = error; }

        observed.ShouldNotBeNull().CancellationToken.ShouldBe(cancellation.Token);
        children.Length.ShouldBe(2);
        foreach (var child in children)
            DefaultProcessingChecks.AssertIdentityStopped(child);
        AssertStagingEmpty();
        await AssertAllSlotsReusableAsync(fixture.Runtime, deadline.Token);
    }

    [Fact]
    public async Task RunAsync_ThirdAdmissionUnexpectedlyGranted_DisposesItAndPreservesOwnedChildrenCleanup()
    {
        var fixture = Setup();
        var unrelatedOptions = Substitute.For<IRomdOptions>();
        unrelatedOptions.DataDirectory.Returns(Path.Combine(_root, "unrelated-admission"));
        var unrelated = new MediaProcessingRuntime(unrelatedOptions, _limits);
        MediaProcessingRuntime.MediaProcessingReservation? unexpected = null;
        MediaProcessIdentity[] children = [];
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));

        var error = await Should.ThrowAsync<InvalidOperationException>(() => DefaultProcessingChecks.RunAsync(
            fixture.Iso, _limits, fixture.Runtime, fixture.Staging, deadline.Token,
            (_, identified, _) => { children = identified.ToArray(); return Task.CompletedTask; },
            async token =>
            {
                var reservation = await unrelated.AcquireAsync(ct: token);
                unexpected = reservation;
                return reservation;
            }));

        error.Message.ShouldContain("third processing reservation must defer");
        unexpected.ShouldNotBeNull();
        File.Exists(unexpected.RecordPath).ShouldBeFalse("even a wrongly granted reservation remains owned by the checker");
        children.Length.ShouldBe(2);
        foreach (var child in children)
            DefaultProcessingChecks.AssertIdentityStopped(child);
        AssertStagingEmpty();
        await AssertAllSlotsReusableAsync(fixture.Runtime, deadline.Token);
        await AssertAllSlotsReusableAsync(unrelated, deadline.Token);
    }

    [Theory]
    [InlineData(MediaWorkerFailure.WallDeadline)]
    [InlineData(MediaWorkerFailure.IoFailure)]
    [InlineData(MediaWorkerFailure.Startup)]
    public void AssertCpuDeadline_UnrelatedFailure_DoesNotQualifyCpuEnforcement(MediaWorkerFailure failure)
    {
        var result = new MediaWorkerResult(null, failure, new(int.MaxValue, 137, 0, 0, MediaWorkerDiagnosticKind.None));
        Should.Throw<InvalidOperationException>(() => SupervisorChecks.AssertCpuDeadline(result, 2, 1, 10));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AssertCpuDeadline_MissingNativeStatusOrZeroExit_FailsClosed(bool missing)
    {
        var result = new MediaWorkerResult(null, MediaWorkerFailure.CpuDeadline,
            missing ? null : new(int.MaxValue, 0, 0, 0, MediaWorkerDiagnosticKind.None));
        Should.Throw<InvalidOperationException>(() => SupervisorChecks.AssertCpuDeadline(result, 2, 1, 10));
    }

    [Fact]
    public void AssertCpuDeadline_NoStopCertificate_DoesNotClaimCapacityRecovery()
    {
        var result = new MediaWorkerResult(null, MediaWorkerFailure.CpuDeadline,
            new(int.MaxValue, 137, 0, 0, MediaWorkerDiagnosticKind.None), ProcessTreeStopped: false);
        Should.Throw<InvalidOperationException>(() => SupervisorChecks.AssertCpuDeadline(result, 2, 1, 10));
    }

    private void AssertStagingEmpty()
    {
        string pool = Path.Combine(_root, "temp", "staging-v1");
        using var guard = new FileStream(Path.Combine(pool, "accounting.lease"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        using var state = JsonDocument.Parse(File.ReadAllText(Path.Combine(pool, "reservations.json")));
        state.RootElement.GetProperty("Reservations").EnumerateObject().ShouldBeEmpty();
        Directory.GetFiles(pool, "*.data").ShouldBeEmpty();
        Directory.GetFiles(pool, "*.quarantine").ShouldBeEmpty();
    }

    private static async Task AssertAllSlotsReusableAsync(MediaProcessingRuntime runtime, CancellationToken ct)
    {
        var acquired = new List<MediaProcessingRuntime.MediaProcessingReservation>();
        try
        {
            for (int slot = 0; slot < 2; slot++)
                acquired.Add(await runtime.AcquireAsync(ct: ct));
            acquired.Count.ShouldBe(2);
        }
        finally { foreach (var reservation in acquired) reservation.Dispose(); }
    }

    // Unknown custody remains available as failure evidence instead of being deleted by teardown.
    public void Dispose() => InspectionChecks.CleanupStaging(_root);
}
