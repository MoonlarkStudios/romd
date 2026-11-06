using System.Diagnostics;
using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Romd.Infrastructure.Storage;
using Shouldly;

namespace Romd.Infrastructure.Tests.Storage;

/// <summary>Isolates immediate OFD-release assertions from other tests' fork/exec windows.</summary>
internal sealed class NativeLeaseProcessFixture : IDisposable
{
    private const string HostAssemblyName = "Romd.NativeLease.TestHost";
    private readonly string _root = Directory.CreateTempSubdirectory("romd-native-lease-host-").FullName;
    private readonly string _assembly;
    internal static bool IsChild => Assembly.GetEntryAssembly()?.GetName().Name == HostAssemblyName;

    internal NativeLeaseProcessFixture()
    {
        _assembly = Path.Combine(_root, "lease-host.dll");
        try
        {
            using var references = new FixtureCompilationReferences(
                ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
                    .Append(typeof(NativeLeaseProcessFixture).Assembly.Location));
            var compilation = CSharpCompilation.Create(HostAssemblyName, [CSharpSyntaxTree.ParseText(Program)], references.References,
                new CSharpCompilationOptions(OutputKind.ConsoleApplication, nullableContextOptions: NullableContextOptions.Enable));
            var emitted = compilation.Emit(_assembly);
            emitted.Success.ShouldBeTrue(string.Join("\n", emitted.Diagnostics));
            File.Copy(Path.ChangeExtension(typeof(NativeLeaseProcessFixture).Assembly.Location, ".runtimeconfig.json"),
                Path.ChangeExtension(_assembly, ".runtimeconfig.json"));
        }
        catch { Dispose(); throw; }
    }

    internal async Task RunAsync(string test, params string[] arguments)
    {
        var start = new ProcessStartInfo(new MediaProcessingOptions().DotnetPath)
        { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string argument in new[] { _assembly, AppContext.BaseDirectory, test }.Concat(arguments))
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start).ShouldNotBeNull();
        Task<string> output = process.StandardOutput.ReadToEndAsync(), error = process.StandardError.ReadToEndAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(40));
        try
        { await process.WaitForExitAsync(deadline.Token); }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }
        }
        process.ExitCode.ShouldBe(0, $"{test} failed in its isolated lease owner:\n{await output}\n{await error}");
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private const string Program = """
        using System;
        using System.Globalization;
        using System.IO;
        using System.Linq;
        using System.Runtime.CompilerServices;
        using System.Runtime.Loader;
        using System.Threading.Tasks;
        using Romd.Infrastructure.Tests.Storage;

        // Nested payload fixtures must resolve the original test outputs, not this temporary host directory.
        AppContext.SetData("APP_CONTEXT_BASE_DIRECTORY", args[0]);
        AssemblyLoadContext.Default.Resolving += (_, name) =>
        {
            string path = Path.Combine(args[0], name.Name + ".dll");
            return File.Exists(path) ? AssemblyLoadContext.Default.LoadFromAssemblyPath(path) : null;
        };
        try
        {
            await RunAsync(args[1], args[2..]);
            return 0;
        }
        catch (Exception failure)
        {
            Console.Error.WriteLine(failure);
            return 1;
        }

        // Resolve the test assembly only after installing its dependency resolver.
        [MethodImpl(MethodImplOptions.NoInlining)]
        static async Task RunAsync(string test, string[] arguments)
        {
            switch (test)
            {
                case nameof(MediaProcessingRuntimeTests.EnsureJobStoppedAsync_AccountingContention_RechecksCustodyAfterLockRelease):
                    using (var runtime = new MediaProcessingRuntimeTests())
                        await runtime.EnsureJobStoppedAsync_AccountingContention_RechecksCustodyAfterLockRelease(bool.Parse(arguments.Single()));
                    break;
                case nameof(MediaProcessingRuntimeTests.EnsureJobStoppedAsync_AccountingRemainsOccupied_TimesOutWithoutChangingCustody):
                    using (var runtime = new MediaProcessingRuntimeTests())
                        await runtime.EnsureJobStoppedAsync_AccountingRemainsOccupied_TimesOutWithoutChangingCustody();
                    break;
                case nameof(MediaProcessingRuntimeTests.EnsureJobStoppedAsync_CanceledWhileAccountingOccupied_PreservesCancellationAndCustody):
                    using (var runtime = new MediaProcessingRuntimeTests())
                        await runtime.EnsureJobStoppedAsync_CanceledWhileAccountingOccupied_PreservesCancellationAndCustody();
                    break;
                case nameof(MediaProcessingRuntimeTests.AcquireAsync_LaunchRecordWithoutIdentity_RemainsPinnedWhenOriginalLeaseCanBeAcquired):
                    using (var runtime = new MediaProcessingRuntimeTests())
                        await runtime.AcquireAsync_LaunchRecordWithoutIdentity_RemainsPinnedWhenOriginalLeaseCanBeAcquired();
                    break;
                case nameof(MediaProcessingRuntimeTests.EnsureJobStoppedAsync_IndependentlyAcquiredLease_ReapsOnlyMatchedAdoptedZombie):
                    using (var runtime = new MediaProcessingRuntimeTests())
                        await runtime.EnsureJobStoppedAsync_IndependentlyAcquiredLease_ReapsOnlyMatchedAdoptedZombie(arguments.Single());
                    break;
                case nameof(MediaProcessingRuntimeTests.UnreapedChild_StartAsync_ClosesUnrelatedInheritableLease):
                    using (var runtime = new MediaProcessingRuntimeTests())
                        await runtime.UnreapedChild_StartAsync_ClosesUnrelatedInheritableLease();
                    break;
                case nameof(PosixMediaToolTests.StartTool_StreamConstructionFails_KillsReapsAndClosesTransferredDescriptors):
                    using (var fixture = new PosixMediaToolFixture())
                        await new PosixMediaToolTests(fixture).StartTool_StreamConstructionFails_KillsReapsAndClosesTransferredDescriptors(int.Parse(arguments.Single(), CultureInfo.InvariantCulture));
                    break;
                default:
                    throw new ArgumentException("Unknown lease test: " + test);
            }
        }
        """;
}
