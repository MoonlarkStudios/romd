using NSubstitute;
using Romd.Infrastructure.Readiness;
using Romd.Infrastructure.Storage;
using Romd.Media.Inspection;
using Romd.Media.PlayStation;
using Romd.Media.TestSupport;
using Shouldly;
using Xunit;

namespace Romd.Hosting.IntegrationTests.Hosting;

public sealed class MediaWorkerStartupTests
{
    [Fact]
    public async Task ValidateAsync_ConfiguredButUnverifiedAssets_FailsClosedAfterManagedHandshake()
    {
        string directory = Directory.CreateTempSubdirectory("romd-chd-startup-").FullName;
        try
        {
            string libchdr = NativeTestBuilds.WriteLibchdrBuild(Path.Combine(directory, "libchdr"), [1, 2, 3], "linux-arm64",
                ChdNativeCandidate.CompiledUpstreamCommit);
            var runtime = new ChdDeploymentRuntime(new() { LibchdrDirectory = libchdr }, false);
            var supervisor = Substitute.For<IMediaWorkerSupervisor>();
            var identity = new MediaWorkerIdentity(123, 123, 512L << 20, "decoder", "worker", null, 512L << 20, 0, null);
            supervisor.HandshakeAsync(CancellationToken.None).Returns(new MediaWorkerResult(new(MediaWorkerStatus.Ready, identity), null));
            var result = await new MediaWorkerStartupValidator(supervisor, deployment: runtime).ValidateAsync();
            result.IsSuccess.ShouldBeFalse();
            result.Failure.ShouldBe(MediaWorkerFailure.Startup);
            runtime.IsReady.ShouldBeFalse();
            await supervisor.Received(1).HandshakeAsync(CancellationToken.None);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Theory]
    [InlineData(MediaWorkerFailure.Startup)]
    [InlineData(MediaWorkerFailure.Protocol)]
    [InlineData(MediaWorkerFailure.OutputLimit)]
    public async Task StartAsync_InvalidChild_FailsBeforeStartupCompletes(MediaWorkerFailure failure)
    {
        var supervisor = Substitute.For<IMediaWorkerSupervisor>();
        using var cancellation = new CancellationTokenSource();
        supervisor.HandshakeAsync(cancellation.Token).Returns(MediaWorkerResult.Failed(failure));
        var service = new MediaWorkerStartupCheck(new MediaWorkerStartupValidator(supervisor));

        var exception = await Should.ThrowAsync<InvalidOperationException>(() => service.StartAsync(cancellation.Token));

        exception.Message.ShouldContain(failure.ToString());
        await supervisor.Received(1).HandshakeAsync(cancellation.Token);
    }

    [Fact]
    public async Task StartAsync_ValidChild_CompletesValidation()
    {
        var supervisor = Substitute.For<IMediaWorkerSupervisor>();
        var identity = new MediaWorkerIdentity(123, 123, 512L << 20, "decoder", "worker", null, 512L << 20, 0, null);
        supervisor.HandshakeAsync(CancellationToken.None).Returns(new MediaWorkerResult(new(MediaWorkerStatus.Ready, identity), null));
        var service = new MediaWorkerStartupCheck(new MediaWorkerStartupValidator(supervisor));

        await service.StartAsync(CancellationToken.None);
        await service.StopAsync(CancellationToken.None);

        await supervisor.Received(1).HandshakeAsync(CancellationToken.None);
    }

    [Fact]
    public async Task StartAsync_CallerCancellation_PreservesCancellation()
    {
        var supervisor = Substitute.For<IMediaWorkerSupervisor>();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var expected = new OperationCanceledException(cancellation.Token);
        supervisor.HandshakeAsync(cancellation.Token).Returns(Task.FromException<MediaWorkerResult>(expected));
        var service = new MediaWorkerStartupCheck(new MediaWorkerStartupValidator(supervisor));
        Exception? observed = null;

        try
        { await service.StartAsync(cancellation.Token); }
        catch (OperationCanceledException exception) { observed = exception; }

        observed.ShouldBeSameAs(expected);
    }
}
