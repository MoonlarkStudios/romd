using Romd.Infrastructure.Readiness;
using Romd.Infrastructure.Storage;
using Romd.Media.Chd;
using Romd.Media.Inspection;
using Romd.Media.TestSupport;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Storage;

/// <summary>Startup verification over synthetic build records and the repository's own native builds.</summary>
public sealed class ChdDeploymentRuntimeTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("romd-chd-runtime-").FullName;
    private static readonly byte[] Binary = [1, 2, 3];

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Constructor_Disabled_IsNotReadyUntilVerification(bool requireTool)
    {
        var runtime = new ChdDeploymentRuntime(new NativeToolOptions(), requireTool);
        runtime.Assets.ShouldBeNull();
        runtime.IsReady.ShouldBeFalse();
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("artifacts/native/libchdr")]
    public void Constructor_InvalidDirectory_DoesNotDisable(string path) =>
        Should.Throw<InvalidOperationException>(() => new ChdDeploymentRuntime(new() { LibchdrDirectory = path }, false));

    [Fact]
    public void Constructor_ApplicationDirectory_HasNoRecordAndFailsClosed() =>
        Should.Throw<ChdNativeCandidateException>(() => new ChdDeploymentRuntime(
            new() { LibchdrDirectory = AppContext.BaseDirectory }, false)).Rejection.Cause.ShouldBe("ManifestMissing");

    [Theory]
    [InlineData("/usr/lib/libSystem.B.dylib")]
    [InlineData("/lib/x86_64-linux-gnu/libz.so.1")]
    [InlineData("../../libmoonlark_chdr.so")]
    public void Constructor_RecordNamingASystemOrOutsideBinary_FailsClosed(string file)
    {
        string libchdr = Libchdr();
        NativeTestBuilds.ChangeRecord(libchdr, node => node["file"] = file);

        Should.Throw<ChdNativeCandidateException>(() => new ChdDeploymentRuntime(new() { LibchdrDirectory = libchdr }, false))
            .Rejection.Cause.ShouldBe("ManifestInvalid");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Constructor_ConfiguredWithMissingBinaries_ConstructsWithoutVerification(bool requireTool)
    {
        string libchdr = Libchdr(), chdman = Chdman();
        File.Delete(Path.Combine(libchdr, "native", "libmoonlark_chdr.so"));
        File.Delete(Path.Combine(chdman, "native", "chdman"));

        var runtime = new ChdDeploymentRuntime(new() { LibchdrDirectory = libchdr, ChdmanDirectory = chdman }, requireTool);

        var assets = runtime.Assets.ShouldNotBeNull();
        assets.Native.BuildDirectory.ShouldBe(libchdr);
        (assets.Tool is not null).ShouldBe(requireTool);
        runtime.IsReady.ShouldBeFalse();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Verify_Disabled_MarksStartupReady(bool requireTool)
    {
        var runtime = new ChdDeploymentRuntime(new NativeToolOptions(), requireTool);
        runtime.Verify();
        runtime.IsReady.ShouldBeTrue();
        runtime.Assets.ShouldBeNull();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Verify_DisabledCancellation_ClearsEarlierReadiness(bool requireTool)
    {
        var runtime = new ChdDeploymentRuntime(new NativeToolOptions(), requireTool);
        runtime.Verify();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        try
        {
            runtime.Verify(cancellation.Token);
            throw new InvalidOperationException("Verification must preserve cancellation.");
        }
        catch (OperationCanceledException error) { error.CancellationToken.ShouldBe(cancellation.Token); }
        runtime.IsReady.ShouldBeFalse();
        runtime.Verify();
        runtime.IsReady.ShouldBeTrue();
    }

    [Fact]
    public void Verify_ConfiguredCancellation_PrecedesMissingNativeFailure()
    {
        var runtime = Configured();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        try
        {
            runtime.Verify(cancellation.Token);
            throw new InvalidOperationException("Verification must preserve cancellation.");
        }
        catch (OperationCanceledException error) { error.CancellationToken.ShouldBe(cancellation.Token); }
        runtime.IsReady.ShouldBeFalse();
    }

    [Fact]
    public void Verify_MissingNative_FailsWithExactCandidateRejection()
    {
        var runtime = Configured();
        File.Delete(runtime.Assets!.Native.BinaryPath);
        var error = Should.Throw<ChdNativeCandidateException>(() => runtime.Verify());
        error.Rejection.Stage.ShouldBe(ChdRejectionStage.NativeVerification);
        error.Rejection.Cause.ShouldBe("BinaryMissing");
        error.Rejection.Path.ShouldBe(runtime.Assets.Native.BinaryPath);
        runtime.IsReady.ShouldBeFalse();
    }

    [Theory]
    [InlineData("BinarySizeMismatch")]
    [InlineData("BinarySha256Mismatch")]
    public void Verify_BadNativeBytes_FailsBeforeLoading(string cause)
    {
        var runtime = Configured();
        byte[] bytes = cause == "BinarySizeMismatch" ? [1] : [1, 2, 4];
        File.WriteAllBytes(runtime.Assets!.Native.BinaryPath, bytes);
        var error = Should.Throw<ChdNativeCandidateException>(() => runtime.Verify());
        error.Rejection.Cause.ShouldBe(cause);
        error.Rejection.Path.ShouldBe(runtime.Assets.Native.BinaryPath);
        runtime.IsReady.ShouldBeFalse();
    }

    [Fact]
    public void Verify_LinkedNativeBinary_FailsBeforeHashingOrLoading()
    {
        var runtime = Configured();
        string elsewhere = Path.Combine(_directory, "elsewhere.so");
        File.Move(runtime.Assets!.Native.BinaryPath, elsewhere);
        File.CreateSymbolicLink(runtime.Assets.Native.BinaryPath, elsewhere);

        var error = Should.Throw<ChdNativeCandidateException>(() => runtime.Verify());

        error.Rejection.Cause.ShouldBe("BinaryNotRegularFile");
        runtime.IsReady.ShouldBeFalse();
    }

    [Fact]
    public void Verify_MissingRecordAfterStartupRead_FailsBeforeLoading()
    {
        var runtime = Configured();
        File.Delete(runtime.Assets!.Native.ManifestPath);
        var error = Should.Throw<ChdNativeCandidateException>(() => runtime.Verify());
        error.Rejection.Cause.ShouldBe("ManifestMissing");
        error.Rejection.Path.ShouldBe(runtime.Assets.Native.ManifestPath);
        runtime.IsReady.ShouldBeFalse();
    }

    [Fact]
    public void Verify_RecordForAnotherRid_FailsBeforeOpeningAssets()
    {
        string wrongRid = NativeTestBuilds.Rid == "linux-arm64" ? "linux-x64" : "linux-arm64";
        var runtime = new ChdDeploymentRuntime(new() { LibchdrDirectory = Libchdr(wrongRid) }, false);
        var error = Should.Throw<ChdNativeCandidateException>(() => runtime.Verify());
        error.Status.ShouldBe(MediaWorkerStatus.Unsupported);
        error.Rejection.Cause.ShouldBe("ProcessRidMismatch");
        runtime.IsReady.ShouldBeFalse();
    }

    [Fact]
    public void Verify_RecordOfAnotherUpstreamPin_FailsBeforeLoading()
    {
        var runtime = new ChdDeploymentRuntime(new() { LibchdrDirectory = Libchdr(pin: new string('1', 40)) }, false);
        var error = Should.Throw<ChdNativeCandidateException>(() => runtime.Verify());
        error.Rejection.Cause.ShouldBe("ManifestPinMismatch");
        runtime.IsReady.ShouldBeFalse();
    }

    [Fact]
    public void Verify_WorkerWithForeignChdmanPin_FailsAfterTheNativeBuildVerifies()
    {
        string chdman = NativeTestBuilds.WriteChdmanBuild(Path.Combine(_directory, "foreign-chdman"), Binary, NativeTestBuilds.Rid,
            "mame0290", ChdmanPin.SourceSha256);
        var runtime = new ChdDeploymentRuntime(
            new() { LibchdrDirectory = NativeTestBuilds.LibchdrDirectory, ChdmanDirectory = chdman }, requireTool: true);

        Should.Throw<ChdmanCandidateException>(() => runtime.Verify()).Rejection.Cause.ShouldBe("ManifestPinMismatch");
        runtime.IsReady.ShouldBeFalse();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Verify_RepositoryBuilds_MarksStartupReady(bool requireTool)
    {
        var runtime = new ChdDeploymentRuntime(new()
        {
            LibchdrDirectory = NativeTestBuilds.LibchdrDirectory,
            ChdmanDirectory = NativeTestBuilds.ChdmanDirectory
        }, requireTool);

        runtime.Verify();

        runtime.IsReady.ShouldBeTrue();
        runtime.Assets.ShouldNotBeNull().Native.ExpectedIdentity.UpstreamCommit.ShouldBe(ChdNativeCandidate.CompiledUpstreamCommit);
    }

    [Fact]
    public void Invalidate_AfterDisabledVerification_ClearsReadiness()
    {
        var runtime = new ChdDeploymentRuntime(new NativeToolOptions(), false);
        runtime.Verify();
        runtime.Invalidate();
        runtime.IsReady.ShouldBeFalse();
    }

    [Fact]
    public async Task StartAsync_Disabled_StopClearsReadiness()
    {
        var runtime = new ChdDeploymentRuntime(new NativeToolOptions(), false);
        var startup = new ChdAssetsStartupCheck(runtime);
        runtime.IsReady.ShouldBeFalse();
        await startup.StartAsync(CancellationToken.None);
        runtime.IsReady.ShouldBeTrue();
        await startup.StopAsync(CancellationToken.None);
        runtime.IsReady.ShouldBeFalse();
    }

    [Fact]
    public async Task StartAsync_CancelledDisabled_PropagatesAndRemainsNotReady()
    {
        var runtime = new ChdDeploymentRuntime(new NativeToolOptions(), false);
        var startup = new ChdAssetsStartupCheck(runtime);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        try
        {
            await startup.StartAsync(cancellation.Token);
            throw new InvalidOperationException("Startup must preserve cancellation.");
        }
        catch (OperationCanceledException error) { error.CancellationToken.ShouldBe(cancellation.Token); }
        runtime.IsReady.ShouldBeFalse();
    }

    [Fact]
    public async Task StartAsync_MissingNative_PropagatesAndRemainsNotReady()
    {
        var runtime = Configured();
        File.Delete(runtime.Assets!.Native.BinaryPath);
        var startup = new ChdAssetsStartupCheck(runtime);
        var error = await Should.ThrowAsync<ChdNativeCandidateException>(() => startup.StartAsync(CancellationToken.None));
        error.Rejection.Cause.ShouldBe("BinaryMissing");
        runtime.IsReady.ShouldBeFalse();
    }

    [Fact]
    public async Task StopAsync_CancelledToken_StillClearsReadiness()
    {
        var runtime = new ChdDeploymentRuntime(new NativeToolOptions(), false);
        var startup = new ChdAssetsStartupCheck(runtime);
        await startup.StartAsync(CancellationToken.None);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await startup.StopAsync(cancellation.Token);
        runtime.IsReady.ShouldBeFalse();
    }

    private ChdDeploymentRuntime Configured() => new(new() { LibchdrDirectory = Libchdr() }, false);

    private string Libchdr(string? rid = null, string? pin = null) => NativeTestBuilds.WriteLibchdrBuild(
        Path.Combine(_directory, "libchdr-" + Guid.NewGuid().ToString("N")), Binary, rid ?? NativeTestBuilds.Rid,
        pin ?? ChdNativeCandidate.CompiledUpstreamCommit);

    private string Chdman() => NativeTestBuilds.WriteChdmanBuild(Path.Combine(_directory, "chdman-" + Guid.NewGuid().ToString("N")),
        Binary, NativeTestBuilds.Rid, ChdmanPin.Tag, ChdmanPin.SourceSha256);

    public void Dispose() => Directory.Delete(_directory, recursive: true);
}
