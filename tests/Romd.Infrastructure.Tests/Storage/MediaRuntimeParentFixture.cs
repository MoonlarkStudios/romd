using System.Diagnostics;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Romd.Admin.Application.Storage.Files;
using Romd.Application.Common.Configuration;
using Romd.Domain.Hashing;
using Romd.Infrastructure.Storage;
using System.IO.Abstractions;
using Shouldly;

namespace Romd.Infrastructure.Tests.Storage;

/// <summary>Real disposable worker parent for SIGKILL/restart tests; reuses only existing assemblies.</summary>
internal sealed class MediaRuntimeParentFixture : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("romd-runtime-parent-").FullName;
    private readonly string _assembly;
    internal MediaRuntimeParentFixture()
    {
        using var references = new FixtureCompilationReferences(
            ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator).Concat(new[]
            {
                typeof(RvzVerifier).Assembly.Location, typeof(ITempFile).Assembly.Location, typeof(IRomdOptions).Assembly.Location,
                typeof(Sha256).Assembly.Location, typeof(FileSystem).Assembly.Location, typeof(IFileSystem).Assembly.Location,
                typeof(ErrorOr.Error).Assembly.Location, typeof(Microsoft.Extensions.Logging.ILogger).Assembly.Location,
                typeof(Romd.Media.Inspection.MediaWorkerProtocol).Assembly.Location
            }));
        _assembly = Path.Combine(_root, "parent.dll");
        var compilation = CSharpCompilation.Create("Romd.Infrastructure.Tests", [CSharpSyntaxTree.ParseText(Program)], references.References,
            new CSharpCompilationOptions(OutputKind.ConsoleApplication, nullableContextOptions: NullableContextOptions.Enable));
        var result = compilation.Emit(_assembly);
        result.Success.ShouldBeTrue(string.Join("\n", result.Diagnostics));
        File.Copy(Path.ChangeExtension(typeof(MediaRuntimeParentFixture).Assembly.Location, ".runtimeconfig.json"), Path.ChangeExtension(_assembly, ".runtimeconfig.json"));
    }
    internal Process Start(string dataDirectory, string workerPath, string sourcePath, string checkpoint, bool encode)
    {
        var start = new ProcessStartInfo(new MediaProcessingOptions().DotnetPath)
        { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string argument in new[] { _assembly, AppContext.BaseDirectory, dataDirectory, workerPath, sourcePath, checkpoint, encode.ToString() })
            start.ArgumentList.Add(argument);
        return Process.Start(start).ShouldNotBeNull();
    }
    public void Dispose() => Directory.Delete(_root, recursive: true);

    private const string Program = """
        using System;
        using System.IO;
        using System.Linq;
        using System.Threading;
        using System.Threading.Tasks;
        using System.Runtime.Loader;
        using System.Runtime.InteropServices;
        using System.Security.Cryptography;
        using System.IO.Abstractions;
        using Romd.Application.Common.Configuration;
        using Romd.Admin.Application.Storage.Files;
        using Romd.Domain.Hashing;
        using Romd.Domain.Source.Rom;
        using Romd.Infrastructure.Storage;
        using Romd.Media.Rvz;
        using Romd.Media.Processes;

        string dependencies = args[0];
        AssemblyLoadContext.Default.Resolving += (_, name) =>
        {
            string path = Path.Combine(dependencies, name.Name + ".dll");
            return File.Exists(path) ? AssemblyLoadContext.Default.LoadFromAssemblyPath(path) : null;
        };
        return await Parent.RunAsync(args[1..], CancellationToken.None);

        static class Parent
        {
            public static async Task<int> RunAsync(string[] args, CancellationToken ct)
            {
                string root = args[0], worker = args[1], original = args[2], checkpoint = args[3];
                bool encode = bool.Parse(args[4]);
                if (checkpoint == "lease-zero")
                {
                    string leasePath = Path.Combine(root, "zero.lease");
                    using (var warm = NativeFileLease.TryAcquire(leasePath)) { if (warm is null) return 70; }
                    if (Close(0) != 0) return 71;
                    var zero = NativeFileLease.TryAcquire(leasePath);
                    if (zero is null || zero.Descriptor != 0) return 72;
                    zero.Dispose();
                    using var reacquired = NativeFileLease.TryAcquire(leasePath);
                    return reacquired is null ? 73 : 0;
                }
                var limits = new MediaProcessingOptions { WorkerPath = worker, MaxConcurrent = 1, AdmissionTimeoutSeconds = 1, WallTimeoutSeconds = 30 };
                var options = new Options(root);
                var runtime = new MediaProcessingRuntime(options, limits);
                var factory = new LocalTempFileFactory(options, new FileSystem(), new TempStorageOptions
                { MaximumBytes = 16L << 20, MaximumFiles = 3, MinimumFreeBytes = 0, AdmissionTimeoutSeconds = 1 });
                long bytes = new FileInfo(original).Length;
                long cap = encode ? ((RvzCanonicalOutputBound.Success)RvzCanonicalBounds.GetGameCubeOutputBound(bytes, limits.MaxLogicalBytes)).MaximumOutputBytes : 0;
                Guid job = Guid.Parse("707b8b7b-2e75-4508-aec9-62593346301d");
                using var processing = await runtime.AcquireAsync(job, "convergence", ct);
                await using var aggregate = (await factory.ReserveAggregateAsync(new(job, "convergence", bytes, cap), ct)).Value;
                processing.TrackAggregate((IMediaStagingAggregate)aggregate);
                using var stream = File.OpenRead(original);
                var hash = Sha256.FromSpan(SHA256.HashData(stream)); stream.Position = 0;
                var artifact = (await aggregate.MaterializeSourceAsync(stream, hash, bytes, ct)).Value;
                await File.WriteAllTextAsync(Path.Combine(root, "source-path"), artifact.Path, ct);
                if (aggregate.Output is { } output) await File.WriteAllTextAsync(Path.Combine(root, "output-path"), output.Path, ct);
                async Task StopAsync()
                {
                    await File.WriteAllTextAsync(Path.Combine(root, "ready"), Environment.ProcessId.ToString(), ct);
                    await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                }
                if (checkpoint == "prepared") await StopAsync();
                var supervisor = new MediaWorkerSupervisor(limits, () => new MediaWorkerProcessLifetime(), setup: async (_, point, token) =>
                {
                    string name = point == MediaWorkerSetupPoint.Spawned ? "spawned" : point == MediaWorkerSetupPoint.Identified ? "identified" : point == MediaWorkerSetupPoint.Permitted ? "permitted" : "other";
                    if (checkpoint != name) return;
                    if (name == "permitted")
                    {
                        string payload = encode ? artifact.Path + ".payload-ready" : artifact.Path + ".descendant.pid";
                        while (!File.Exists(payload)) await Task.Delay(10, token);
                    }
                    await StopAsync();
                }, runtime: runtime);
                using var verifier = new RvzVerifier(limits, supervisor, runtime);
                using var active = processing.Activate();
                if (encode)
                {
                    using var admission = await verifier.AcquireAsync(ct);
                    var result = await admission.EncodeAsync(artifact, aggregate.Output!, bytes, RomIsoDiscKind.GameCube, ct);
                    if (result.IsError) return 21;
                    var facts = result.Value;
                    var encodedArtifact = (await aggregate.Output!.SealAsync(new(Sha256.Parse(facts.ArtifactSha256), Md5.Parse(facts.ArtifactMd5), Sha1.Parse(facts.ArtifactSha1), facts.ArtifactBytes), ct)).Value;
                    processing.TrackAggregate((IMediaStagingAggregate)aggregate);
                    if ((await verifier.VerifyAsync(encodedArtifact, ct)).IsError) return 22;
                }
                else if ((await verifier.VerifyAsync(artifact, ct)).IsError) return 23;
                if (checkpoint == "verified") await StopAsync();
                return 0;
            }
            private sealed class Options(string root) : IRomdOptions
            {
                public string DataDirectory => root;
                public string? DefaultAdminEmail => null;
                public string? DefaultAdminPassword => null;
                public string JwtSecret => string.Empty;
                public long MaxUploadBytes => 8L << 20;
                public string[] AllowedImportPaths => [];
                public string LogsDirectory => root;
            }
            [DllImport("libc", EntryPoint = "close", SetLastError = true)] private static extern int Close(int descriptor);
        }
        """;
}
