using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Romd.Application.Common.Readiness;
using Romd.Infrastructure;
using Romd.Infrastructure.Readiness;
using Romd.Infrastructure.Storage;
using Romd.Media.PlayStation;
using Romd.Media.TestSupport;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Readiness;

public sealed class ChdAssetsReadinessCheckTests
{
    [Fact]
    public async Task EvaluateAsync_StartupAndStop_GateCriticalReadiness()
    {
        var runtime = new ChdDeploymentRuntime(new NativeToolOptions(), false);
        var check = new ChdAssetsReadinessCheck(runtime);
        check.IsCritical.ShouldBeTrue();
        check.Name.ShouldBe(ReadinessCheckNames.NativeMedia);
        (await check.EvaluateAsync(CancellationToken.None)).ShouldBe(ReadinessCheckStatus.Unready);
        var startup = new ChdAssetsStartupCheck(runtime);
        await startup.StartAsync(CancellationToken.None);
        (await check.EvaluateAsync(CancellationToken.None)).ShouldBe(ReadinessCheckStatus.Healthy);
        await startup.StopAsync(CancellationToken.None);
        (await check.EvaluateAsync(CancellationToken.None)).ShouldBe(ReadinessCheckStatus.Unready);
    }

    [Fact]
    public async Task EvaluateAsync_Cancelled_DoesNotReportHealthy()
    {
        var runtime = new ChdDeploymentRuntime(new NativeToolOptions(), false);
        runtime.Verify();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Should.ThrowAsync<OperationCanceledException>(() =>
            new ChdAssetsReadinessCheck(runtime).EvaluateAsync(cancellation.Token));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Composition_ConfiguredMissingBuildRecord_FailsWhenHostedServicesResolve(bool requireTool)
    {
        var services = new ServiceCollection();
        var missing = Path.Combine(Path.GetTempPath(), $"romd-missing-chd-{Guid.NewGuid():N}");
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["Romd:Native:LibchdrDirectory"] = missing,
                ["Romd:Native:ChdmanDirectory"] = missing + "-chdman"
            }).Build());
        services.AddRomdChdAssets(requireTool);
        using var provider = services.BuildServiceProvider();
        Should.Throw<ChdNativeCandidateException>(() => provider.GetServices<IHostedService>().ToArray())
            .Rejection.Cause.ShouldBe("ManifestMissing");
    }

    [Fact]
    public void Composition_NativeDirectories_BindTheTypedOptions()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["Romd:Native:LibchdrDirectory"] = "/opt/romd/libchdr",
                ["Romd:Native:ChdmanDirectory"] = "/opt/romd/chdman"
            }).Build());
        services.AddRomdChdAssets(false);
        using var provider = services.BuildServiceProvider();

        var options = provider.GetRequiredService<NativeToolOptions>();

        options.LibchdrDirectory.ShouldBe("/opt/romd/libchdr");
        options.ChdmanDirectory.ShouldBe("/opt/romd/chdman");
    }

    [Fact]
    public async Task Composition_RepeatedRegistration_UsesOneStartupGateAndSharedRuntime()
    {
        var services = new ServiceCollection();
        services.AddRomdChdAssets(false).AddRomdChdAssets(true).AddRomdChdAssets(false);
        using var provider = services.BuildServiceProvider();
        var runtime = provider.GetRequiredService<ChdDeploymentRuntime>();
        var startup = provider.GetServices<IHostedService>().Single();
        runtime.IsReady.ShouldBeFalse();
        await startup.StartAsync(CancellationToken.None);
        runtime.IsReady.ShouldBeTrue();
        await startup.StopAsync(CancellationToken.None);
        runtime.IsReady.ShouldBeFalse();
    }

    [Fact]
    public void Composition_WorkerRequirement_SurvivesLaterNativeOnlyRegistration()
    {
        string directory = Directory.CreateTempSubdirectory("romd-chd-registration-").FullName;
        try
        {
            string libchdr = NativeTestBuilds.WriteLibchdrBuild(Path.Combine(directory, "libchdr"), [1, 2, 3], "linux-arm64",
                ChdNativeCandidate.CompiledUpstreamCommit);
            var services = new ServiceCollection();
            services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(
                new Dictionary<string, string?> { ["Romd:Native:LibchdrDirectory"] = libchdr }).Build());
            services.AddRomdChdAssets(false).AddRomdChdAssets(true).AddRomdChdAssets(false);
            using var provider = services.BuildServiceProvider();
            Should.Throw<InvalidOperationException>(() => provider.GetRequiredService<ChdDeploymentRuntime>())
                .Message.ShouldBe("Worker CHD encoding requires Romd:Native:ChdmanDirectory.");
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
