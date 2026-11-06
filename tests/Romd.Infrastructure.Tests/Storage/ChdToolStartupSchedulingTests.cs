using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Romd.Media.Inspection;
using Romd.Media.PlayStation;
using Romd.Media.Processes;
using Romd.Media.TestSupport;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Storage;

public sealed class ChdToolStartupSchedulingTests
{
    [Theory]
    [InlineData("success")]
    [InlineData("cancel")]
    [InlineData("replaced-image")]
    public async Task Linux_SealedLaunchOnOnePoolThread_RunsOrRefusesAndAllPathsJoin(string mode)
    {
        string directory = Directory.CreateDirectory(Path.Combine(OperatingSystem.IsMacOS() ? "/private/tmp" : Path.GetTempPath(),
            "romd-tool-startup-" + Guid.NewGuid().ToString("N"))).FullName;
        try
        {
            byte[] source = new byte[32768];
            Encoding.ASCII.GetBytes("ROMD-DVD-FIXTURE:23\n").CopyTo(source, 0);
            string sourcePath = Path.Combine(directory, "source.iso");
            File.WriteAllBytes(sourcePath, source);
            if (!OperatingSystem.IsLinux())
            {
                var unsupported = await ChdDvdEncodingService.EncodeAsync(sourcePath, Path.Combine(directory, "artifact.chd"),
                    new(Path.Combine(directory, "missing-tool"), new("unsupported", ChdmanPin.Tag, ChdmanPin.SourceSha256, 1, 1, new string('a', 64))),
                    source.Length, Convert.ToHexStringLower(SHA256.HashData(source)), source.Length, source.Length, 8192, "unix-nice", 10, -1);
                unsupported.Status.ShouldBe(MediaWorkerStatus.Unsupported);
                unsupported.Rejection.ShouldNotBeNull().Cause.ShouldBe(ChdDvdEncodingCause.LinuxRequired);
                return;
            }
            string packaged = Path.Combine(AppContext.BaseDirectory, "media-worker");
            foreach (string file in Directory.EnumerateFiles(packaged))
                File.Copy(file, Path.Combine(directory, Path.GetFileName(file)));
            using var references = new FixtureCompilationReferences(
                ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
                    .Append(typeof(MediaWorkerProtocol).Assembly.Location));
            var compilation = CSharpCompilation.Create("Romd.Media.Worker", [CSharpSyntaxTree.ParseText(ChildProgram)],
                references.References, new CSharpCompilationOptions(OutputKind.ConsoleApplication));
            string worker = Path.Combine(directory, "Romd.Media.Worker.dll");
            var emitted = compilation.Emit(worker);
            emitted.Success.ShouldBeTrue(string.Join("\n", emitted.Diagnostics));
            string build = NativeTestBuilds.CompileChdDvdToolFixture(Path.Combine(directory, "tool"),
                ChdmanPin.Tag, ChdmanPin.SourceSha256);
            var start = new ProcessStartInfo("nice") { RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (string argument in new[] { "-n", "10", "dotnet", worker, directory, build, mode })
                start.ArgumentList.Add(argument);
            var result = await RunOwnedAsync(start);
            result.Exit.ShouldBe(0, result.Output + result.Error);
            result.Output.ShouldContain("joined:" + mode);
            // A forgotten inherited admission descriptor would retain this flock after the probe exits.
            using var released = NativeFileLease.TryAcquire(Path.Combine(directory, "lease"));
            released.ShouldNotBeNull();
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private static async Task<(int Exit, string Output, string Error)> RunOwnedAsync(ProcessStartInfo start)
    {
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Fixture child did not start.");
        Task<string> stdout = process.StandardOutput.ReadToEndAsync(), stderr = process.StandardError.ReadToEndAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        try
        {
            // The deadline lives in the parent: a starved child cannot be trusted to run its timer.
            await process.WaitForExitAsync(deadline.Token);
            return (process.ExitCode, await stdout, await stderr);
        }
        finally
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            await Task.WhenAll(stdout, stderr);
        }
    }

    // One pool thread: the drains, the exit wait and the joins must all complete without a second worker.
    private const string ChildProgram = """
        using System;
        using System.IO;
        using System.Text.Json;
        using System.Threading;
        using System.Threading.Tasks;
        using Romd.Media.PlayStation;
        using Romd.Media.Processes;
        string directory = args[0], mode = args[2];
        string source = Path.Combine(directory, "source.iso"), artifact = Path.Combine(directory, "artifact.chd");
        var candidate = ChdmanCandidate.FromBuildRecord(args[1]); candidate.Verify();
        if (mode == "replaced-image")
        {
            // After verification, only the sealed bytes decide what may execute.
            byte[] changed = File.ReadAllBytes(candidate.BinaryPath); changed[^1] ^= 0xff;
            File.Move(candidate.BinaryPath, candidate.BinaryPath + ".verified");
            File.WriteAllBytes(candidate.BinaryPath, changed);
            File.SetUnixFileMode(candidate.BinaryPath, File.GetUnixFileMode(candidate.BinaryPath + ".verified"));
        }
        using var lease = NativeFileLease.TryAcquire(Path.Combine(directory, "lease"));
        if (lease is null) throw new Exception("Admission lease missing.");
        string nonce = Guid.NewGuid().ToString("N"); lease.Bind(nonce);
        var inherited = ChdmanToolRunner.CurrentExpectation();
        if (!ThreadPool.SetMinThreads(1, 1) || !ThreadPool.SetMaxThreads(1, 1)) throw new Exception("Pool configuration failed.");
        var token = mode == "cancel" ? new CancellationToken(true) : CancellationToken.None;
        try {
            var result = Task.Run(() => ChdmanToolRunner.RunAsync(candidate, ChdDvdCanonicalRecipe.ChdmanArguments(source, artifact),
                8192, lease.Descriptor, inherited, token)).GetAwaiter().GetResult();
            if (mode == "cancel") throw new Exception("Cancellation did not propagate.");
            if (mode == "replaced-image") {
                if (result.Rejection?.Cause != ChdDvdEncodingCause.ToolImageMismatch || result.Diagnostics != null)
                    throw new Exception("A replaced image was not refused before launch.");
                if (File.Exists(artifact) || File.Exists(source + ".pid")) throw new Exception("A replaced image started a process.");
            } else {
                var diagnostics = result.Diagnostics ?? throw new Exception($"Short tool did not run: {result.Rejection?.Cause}.");
                if (Directory.Exists($"/proc/{diagnostics.ProcessId}")) throw new Exception("Native child was not joined.");
                if (result.Rejection != null || diagnostics.ExitCode != 0 || !File.Exists(artifact))
                    throw new Exception($"Short tool rejected: {result.Rejection?.Cause}; exit {diagnostics.ExitCode}.");
                if (diagnostics.StandardOutputBytes > 8192 || diagnostics.StandardErrorBytes > 8192)
                    throw new Exception("Diagnostic budget exceeded.");
                using var facts = JsonDocument.Parse(File.ReadAllBytes(source + ".facts")); var f = facts.RootElement;
                if (f.GetProperty("Pid").GetInt32() != diagnostics.ProcessId || f.GetProperty("Nice").GetInt32() != inherited.Nice ||
                    f.GetProperty("FsizeSoft").GetUInt64() != 8192 || f.GetProperty("FsizeHard").GetUInt64() != 8192 ||
                    f.GetProperty("ExecutesToolImage").GetInt32() != 1 || f.GetProperty("ExtraDescriptor").GetInt32() != 0 ||
                    f.GetProperty("ToolImageSeals").GetInt32() != SealedExecutable.ImmutableSeals ||
                    f.GetProperty("Nonce").GetString() != nonce || f.GetProperty("FlockDenied").GetInt32() != 1 ||
                    f.GetProperty("EnvironmentCount").GetInt32() != 0) throw new Exception("Inherited custody/resource facts differ.");
            }
        } catch (OperationCanceledException error) when (mode == "cancel" && error.CancellationToken == token) { }
        foreach (string thread in Directory.EnumerateDirectories("/proc/self/task")) {
            try { if (!string.IsNullOrWhiteSpace(File.ReadAllText(Path.Combine(thread, "children"))))
                throw new Exception("A native child remains unjoined."); }
            catch (DirectoryNotFoundException) { } catch (FileNotFoundException) { }
        }
        Console.WriteLine("joined:" + mode);
        """;
}
