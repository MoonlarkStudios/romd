using System.Diagnostics;
using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Romd.Infrastructure.Storage;
using Romd.Media.Chd;
using Romd.Media.TestSupport;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Storage;

/// <summary>CHD composition in a fresh process whose application directory holds a loadable libchdr copy.</summary>
/// <remarks>libchdr stays resident once loaded, so only a separate process can show that nothing loaded it. The planted
/// copy is exactly what the wrapper's own default search would find if any code used the wrapper before verification.</remarks>
public sealed class ChdNativeIsolationTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("romd-chd-isolation-").FullName;

    [Fact]
    public async Task HostAndWorkerComposition_WithoutOrWithBrokenNativeDirectories_NeverLoadTheApplicationDirectoryCopy()
    {
        string app = Directory.CreateDirectory(Path.Combine(_root, "app")).FullName;
        var native = ChdNativeCandidate.FromBuildRecord(NativeTestBuilds.LibchdrDirectory).ExpectedIdentity;
        File.Copy(Path.Combine(NativeTestBuilds.LibchdrDirectory, "native", native.File), Path.Combine(app, native.File));
        string broken = NativeTestBuilds.CopyBuild(NativeTestBuilds.LibchdrDirectory, Path.Combine(_root, "broken"));
        File.Delete(Path.Combine(broken, "native", native.File));

        using JsonDocument report = JsonDocument.Parse(await RunChildAsync(CompileChild(app), broken));
        JsonElement result = report.RootElement;

        result.GetProperty("offAssets").GetBoolean().ShouldBeFalse();
        result.GetProperty("offReady").GetBoolean().ShouldBeTrue();
        result.GetProperty("offChdAdapter").GetBoolean().ShouldBeFalse();
        result.GetProperty("offMediaAdapter").GetString().ShouldBe("CHD reading requires configured native assets.");
        result.GetProperty("offResident").GetBoolean().ShouldBeFalse();
        result.GetProperty("offMapped").GetBoolean().ShouldBeFalse();
        result.GetProperty("workerReady").GetBoolean().ShouldBeTrue();
        result.GetProperty("workerVerifier").GetString().ShouldBe("CHD preparation requires configured native assets.");
        result.GetProperty("workerEncoder").GetString().ShouldBe("Native encoding requires verified tool assets.");
        result.GetProperty("workerResident").GetBoolean().ShouldBeFalse();
        result.GetProperty("workerMapped").GetBoolean().ShouldBeFalse();
        result.GetProperty("brokenCause").GetString().ShouldBe("BinaryMissing");
        result.GetProperty("brokenReady").GetBoolean().ShouldBeFalse();
        result.GetProperty("brokenResident").GetBoolean().ShouldBeFalse();
        result.GetProperty("brokenMapped").GetBoolean().ShouldBeFalse();
        // Control: the planted copy really is loadable through the wrapper's default search, which ROMD never uses.
        result.GetProperty("controlResident").GetBoolean().ShouldBeTrue();
        result.GetProperty("controlMapped").GetBoolean().ShouldBeTrue();
    }

    private static string CompileChild(string directory)
    {
        string assembly = Path.Combine(directory, "probe.dll");
        string[] trusted = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator);
        var trustedNames = trusted.Select(Path.GetFileNameWithoutExtension).ToHashSet(StringComparer.OrdinalIgnoreCase);
        using var references = new FixtureCompilationReferences(
            trusted.Concat(Directory.EnumerateFiles(AppContext.BaseDirectory, "*.dll")
                .Where(path => !trustedNames.Contains(Path.GetFileNameWithoutExtension(path)))));
        var compilation = CSharpCompilation.Create("Romd.Chd.IsolationProbe", [CSharpSyntaxTree.ParseText(Program)], references.References,
            new CSharpCompilationOptions(OutputKind.ConsoleApplication, nullableContextOptions: NullableContextOptions.Enable));
        var emitted = compilation.Emit(assembly);
        emitted.Success.ShouldBeTrue(string.Join("\n", emitted.Diagnostics));
        File.Copy(Path.ChangeExtension(typeof(ChdNativeIsolationTests).Assembly.Location, ".runtimeconfig.json"),
            Path.ChangeExtension(assembly, ".runtimeconfig.json"));
        return assembly;
    }

    private static async Task<string> RunChildAsync(string assembly, string broken)
    {
        var start = new ProcessStartInfo(new MediaProcessingOptions().DotnetPath)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (string argument in new[] { assembly, AppContext.BaseDirectory, broken })
            start.ArgumentList.Add(argument);
        using var child = Process.Start(start) ?? throw new InvalidOperationException("The isolation probe did not start.");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        Task<string> output = child.StandardOutput.ReadToEndAsync(deadline.Token), error = child.StandardError.ReadToEndAsync(deadline.Token);
        await child.WaitForExitAsync(deadline.Token);
        child.ExitCode.ShouldBe(0, await error);
        return await output;
    }

    private const string Program = """
        using System;
        using System.Collections.Generic;
        using System.Diagnostics;
        using System.IO;
        using System.Linq;
        using System.Reflection;
        using System.Runtime.CompilerServices;
        using System.Runtime.InteropServices;
        using System.Runtime.Loader;
        using System.Text.Json;
        using System.Threading;
        using System.Threading.Tasks;
        using Microsoft.Extensions.Configuration;
        using Microsoft.Extensions.DependencyInjection;
        using Microsoft.Extensions.Hosting;
        using Romd.Infrastructure;
        using Romd.Infrastructure.Readiness;
        using Romd.Infrastructure.Storage;
        using Romd.Media.Chd;

        AssemblyLoadContext.Default.Resolving += (_, name) =>
        {
            string path = Path.Combine(args[0], name.Name + ".dll");
            return File.Exists(path) ? AssemblyLoadContext.Default.LoadFromAssemblyPath(path) : null;
        };
        return await Probe.RunAsync(args[1]);

        static class Probe
        {
            [MethodImpl(MethodImplOptions.NoInlining)]
            public static async Task<int> RunAsync(string broken)
            {
                var report = new Dictionary<string, object?>();
                using (var off = Compose(null))
                {
                    foreach (var hosted in off.GetServices<IHostedService>()) await hosted.StartAsync(CancellationToken.None);
                    var runtime = off.GetRequiredService<ChdDeploymentRuntime>();
                    report["offAssets"] = runtime.Assets is not null;
                    report["offReady"] = runtime.IsReady &&
                        await new ChdAssetsReadinessCheck(runtime).EvaluateAsync(CancellationToken.None) == Romd.Application.Common.Readiness.ReadinessCheckStatus.Healthy;
                    report["offChdAdapter"] = typeof(RomMediaAdapterRegistry)
                        .GetMethod("Supports", BindingFlags.NonPublic | BindingFlags.Instance)!
                        .Invoke(off.GetRequiredService<RomMediaAdapterRegistry>(), new object[] { Romd.Domain.Source.Rom.RomRepresentationEncoding.Chd }) is true;
                    off.GetRequiredService<Romd.Application.Common.Content.IRomChdDeliveryAdmission>();
                    try { off.GetRequiredService<ChdMediaAdapter>(); report["offMediaAdapter"] = "resolved"; }
                    catch (NotSupportedException error) { report["offMediaAdapter"] = error.Message; }
                    report["offResident"] = Resident();
                    report["offMapped"] = Mapped();
                }
                using (var worker = Compose(null, worker: true))
                {
                    // The worker's other hosted checks spawn the media child; only the native-build gate matters here.
                    var runtime = worker.GetRequiredService<ChdDeploymentRuntime>();
                    await new ChdAssetsStartupCheck(runtime).StartAsync(CancellationToken.None);
                    report["workerReady"] = runtime.IsReady;
                    try { worker.GetRequiredService<ChdVerifier>(); report["workerVerifier"] = "resolved"; }
                    catch (NotSupportedException error) { report["workerVerifier"] = error.Message; }
                    try { worker.GetRequiredService<ChdArtifactEncoder>(); report["workerEncoder"] = "resolved"; }
                    catch (NotSupportedException error) { report["workerEncoder"] = error.Message; }
                    report["workerResident"] = Resident();
                    report["workerMapped"] = Mapped();
                }
                using (var configured = Compose(broken))
                {
                    var runtime = configured.GetRequiredService<ChdDeploymentRuntime>();
                    try
                    {
                        foreach (var hosted in configured.GetServices<IHostedService>()) await hosted.StartAsync(CancellationToken.None);
                        report["brokenCause"] = "started";
                    }
                    catch (ChdNativeCandidateException error) { report["brokenCause"] = error.Rejection.Cause; }
                    report["brokenReady"] = runtime.IsReady;
                    report["brokenResident"] = Resident();
                    report["brokenMapped"] = Mapped();
                }
                _ = Moonlark.Libchdr.LibchdrLibrary.BuildInfo;
                report["controlResident"] = Resident();
                report["controlMapped"] = Mapped();
                Console.Out.Write(JsonSerializer.Serialize(report));
                return 0;
            }

            private static ServiceProvider Compose(string? libchdr, bool worker = false)
            {
                var values = new Dictionary<string, string?>();
                if (libchdr is not null) values["Romd:Native:LibchdrDirectory"] = libchdr;
                var services = new ServiceCollection();
                services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(values).Build());
                services.AddRomdReadOnlyStorage();
                services.AddRomdStorage();
                if (worker) services.AddRomdWorkerMediaProcessing();
                return services.BuildServiceProvider();
            }

            private static bool Resident() => typeof(Moonlark.Libchdr.LibchdrLibrary)
                .GetField("_resident", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null) is not null;

            // Process.Modules omits dlopen'ed images on macOS, so ask the loader itself.
            private static bool Mapped()
            {
                if (OperatingSystem.IsLinux())
                    return File.ReadAllText("/proc/self/maps").Contains("moonlark_chdr", StringComparison.Ordinal);
                if (!OperatingSystem.IsMacOS()) throw new PlatformNotSupportedException("No image inventory for this platform.");
                uint count = Dyld.ImageCount();
                for (uint index = 0; index < count; index++)
                    if (Marshal.PtrToStringUTF8(Dyld.ImageName(index)) is { } name && name.Contains("moonlark_chdr", StringComparison.Ordinal))
                        return true;
                return false;
            }
        }

        static class Dyld
        {
            [DllImport("/usr/lib/libSystem.B.dylib", EntryPoint = "_dyld_image_count")]
            public static extern uint ImageCount();

            [DllImport("/usr/lib/libSystem.B.dylib", EntryPoint = "_dyld_get_image_name")]
            public static extern IntPtr ImageName(uint index);
        }
        """;

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
