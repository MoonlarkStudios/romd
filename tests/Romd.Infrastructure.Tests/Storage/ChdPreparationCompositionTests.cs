using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Romd.Admin.Application.Hashing;
using Romd.Admin.Application.Source.Rom;
using Romd.Application.Common.Configuration;
using Romd.Application.Common.Content;
using Romd.Infrastructure.Storage;
using Romd.Media.PlayStation;
using Romd.Media.TestSupport;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Storage;

/// <summary>Production registration and admission; no native load, child execution or readiness claim.</summary>
public sealed class ChdPreparationCompositionTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("romd-chd-composition-").FullName;

    [Fact]
    public void ProductionRegistry_NoNativeDirectories_OrdinaryRoutesRemainAvailableWithoutResolvingChd()
    {
        using var provider = Services(configured: false).BuildServiceProvider();
        var registry = provider.GetRequiredService<RomPreparationAdapterRegistry>();

        registry.Detect("plain"u8, "game.bin").RequiresDatHashes("plain"u8).ShouldBeTrue();
        registry.Detect("RVZ\u0001"u8, "game.rvz").RequiresDatHashes("RVZ\u0001"u8).ShouldBeFalse();
        provider.GetRequiredService<ChdDeploymentRuntime>().Assets.ShouldBeNull();
        Directory.Exists(Path.Combine(_directory, "temp", "media-runs-v1")).ShouldBeFalse();
    }

    [Fact]
    public async Task ProductionRegistry_NoNativeDirectories_ChdAdmissionRefusesBeforeCapacityOrFallback()
    {
        using var provider = Services(configured: false).BuildServiceProvider();
        var adapter = provider.GetRequiredService<RomPreparationAdapterRegistry>().Detect("MComprHD"u8, "game.iso");
        adapter.RequiresDatHashes(MediaInfrastructureTestServices.ChdV5Prefix(2048)).ShouldBeFalse();

        var error = await Should.ThrowAsync<NotSupportedException>(() => adapter.AdmitAsync(MediaInfrastructureTestServices.ChdV5Prefix(2048), CancellationToken.None));

        error.Message.ShouldBe("CHD preparation requires configured native assets.");
        Directory.Exists(Path.Combine(_directory, "temp", "media-runs-v1")).ShouldBeFalse();
    }

    [Fact]
    public async Task ProductionRegistry_ConfiguredDirectories_UsesRegisteredVerifierAndReleasesUnusedAdmission()
    {
        var supervisor = Substitute.For<IMediaWorkerSupervisor>();
        var services = Services(configured: true);
        services.AddSingleton(supervisor);
        using var provider = services.BuildServiceProvider();
        var registry = provider.GetRequiredService<RomPreparationAdapterRegistry>();
        var verifier = provider.GetRequiredService<ChdVerifier>();
        verifier.ShouldBeSameAs(provider.GetRequiredService<ChdVerifier>());
        var deployment = provider.GetRequiredService<ChdDeploymentRuntime>();
        deployment.Assets.ShouldNotBeNull().Native.ExpectedIdentity
            .ShouldBe(ChdNativeCandidate.FromBuildRecord(Path.Combine(_directory, "libchdr")).ExpectedIdentity);
        deployment.IsReady.ShouldBeFalse();

        using (var admission = await registry.Detect("MComprHD"u8, "game.iso").AdmitAsync(MediaInfrastructureTestServices.ChdV5Prefix(2048), CancellationToken.None))
            admission.ShouldNotBeNull();

        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var next = await provider.GetRequiredService<MediaProcessingRuntime>().AcquireAsync(ct: deadline.Token);
        supervisor.ReceivedCalls().ShouldBeEmpty();
        File.Exists(deployment.Assets.Native.BinaryPath).ShouldBeFalse();
    }

    [Fact]
    public void WorkerComposition_DisabledAssets_RegistersLazyEncoderThatRefusesBeforeAdmission()
    {
        var services = Services(configured: false);
        var registrations = services.Where(descriptor => descriptor.ServiceType == typeof(ChdArtifactEncoder)).ToArray();
        registrations.Length.ShouldBe(1);
        registrations[0].Lifetime.ShouldBe(ServiceLifetime.Singleton);
        using var provider = services.BuildServiceProvider();

        var error = Should.Throw<NotSupportedException>(() => provider.GetRequiredService<ChdArtifactEncoder>());

        error.Message.ShouldBe("Native encoding requires verified tool assets.");
        provider.GetRequiredService<ChdDeploymentRuntime>().Assets.ShouldBeNull();
        Directory.Exists(Path.Combine(_directory, "temp", "media-runs-v1")).ShouldBeFalse();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WorkerComposition_ConfiguredUnverifiedAssets_RefusesBeforeCapacityOrMediaResolution(bool resolveMedia)
    {
        var services = Services(configured: true);
        services.AddRomdStorage();
        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        using var scope = provider.CreateScope();
        var deployment = provider.GetRequiredService<ChdDeploymentRuntime>();
        var assets = deployment.Assets.ShouldNotBeNull();
        assets.Tool.ShouldNotBeNull();
        deployment.IsReady.ShouldBeFalse();

        var error = Should.Throw<NotSupportedException>(() =>
        {
            if (resolveMedia)
                scope.ServiceProvider.GetRequiredService<Romd.Admin.Application.Source.Rom.Reconciliation.IMediaReconciliationMediaProfile>();
            scope.ServiceProvider.GetRequiredService<ChdArtifactEncoder>();
        });

        error.Message.ShouldBe("Native encoding requires verified tool assets.");
        deployment.IsReady.ShouldBeFalse();
        Directory.Exists(Path.Combine(_directory, "temp", "media-runs-v1")).ShouldBeFalse();
        File.Exists(assets.Native.BinaryPath).ShouldBeFalse();
        File.Exists(assets.Tool!.BinaryPath).ShouldBeFalse();
    }

    [Fact]
    public void WorkerComposition_ConfiguredWithoutTool_RefusesDeploymentBeforeMediaResolution()
    {
        using var provider = Services(configured: true, includeTool: false).BuildServiceProvider();

        var error = Should.Throw<InvalidOperationException>(() => provider.GetRequiredService<ChdDeploymentRuntime>());

        error.Message.ShouldBe("Worker CHD encoding requires Romd:Native:ChdmanDirectory.");
        Directory.Exists(Path.Combine(_directory, "temp", "media-runs-v1")).ShouldBeFalse();
    }

    [Theory]
    [InlineData("admin", false)]
    [InlineData("consumer", false)]
    [InlineData("worker", true)]
    [InlineData("single-host", true)]
    public void HostComposition_MediaCapabilities_OnlyTheWorkerEncodes(string host, bool encodes)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        // The worker registers media processing before storage; the single-host and test compositions register it after.
        if (host == "worker")
            services.AddRomdWorkerMediaProcessing();
        if (host == "consumer")
            services.AddRomdReadOnlyStorage();
        else
            services.AddRomdStorage();
        if (host == "single-host")
            services.AddRomdWorkerMediaProcessing();
        using var provider = services.BuildServiceProvider();

        var capabilities = provider.GetRequiredService<IMediaHostCapabilities>();

        services.Count(descriptor => descriptor.ServiceType == typeof(IMediaHostCapabilities)).ShouldBe(1);
        // RVZ encodes with no native build, so it answers only for the host role.
        capabilities.CanEncode("gamecube-disc").ShouldBe(encodes);
        capabilities.CanDecode("gamecube-disc").ShouldBeTrue();
        // Without native builds no host decodes or encodes CHD.
        capabilities.CanDecode("ps1-cd-set").ShouldBeFalse();
        capabilities.CanEncode("ps1-cd-set").ShouldBeFalse();
    }

    // Synthetic records whose binaries are absent: composition must read the records without opening either binary.
    private ServiceCollection Services(bool configured, bool includeTool = true)
    {
        var options = Substitute.For<IRomdOptions>();
        options.DataDirectory.Returns(_directory);
        var values = new Dictionary<string, string?> { ["Romd:Media:MaxConcurrent"] = "1" };
        if (configured)
        {
            string libchdr = NativeTestBuilds.WriteLibchdrBuild(Path.Combine(_directory, "libchdr"), [1, 2, 3], "linux-arm64",
                ChdNativeCandidate.CompiledUpstreamCommit);
            File.Delete(Path.Combine(libchdr, "native", "libmoonlark_chdr.so"));
            values["Romd:Native:LibchdrDirectory"] = libchdr;
            if (includeTool)
            {
                string chdman = NativeTestBuilds.WriteChdmanBuild(Path.Combine(_directory, "chdman"), [4, 5, 6], "linux-arm64",
                    ChdmanPin.Tag, ChdmanPin.SourceSha256);
                File.Delete(Path.Combine(chdman, "native", "chdman"));
                values["Romd:Native:ChdmanDirectory"] = chdman;
            }
        }
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        var services = new ServiceCollection();
        services.AddSingleton(options);
        services.AddSingleton<IConfiguration>(configuration);
        services.AddSingleton(Substitute.For<IHashingService>());
        services.AddRomdWorkerMediaProcessing();
        return services;
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);
}
