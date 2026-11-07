using System.Diagnostics;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Romd.Infrastructure.Storage;
using Romd.Media.Processes;
using Shouldly;

namespace Romd.Infrastructure.Tests.Storage;

/// <summary>A small real tool payload; uses the existing process/descriptor observers, with no new interop.</summary>
public sealed class PosixMediaToolFixture : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("romd-posix-tool-fixture-").FullName;
    private readonly string _assembly;
    internal string TemporaryDirectory => _root;

    public PosixMediaToolFixture()
    {
        _assembly = Path.Combine(_root, "tool.dll");
        using var references = new FixtureCompilationReferences(
            ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
                .Concat([typeof(MediaProcessIdentity).Assembly.Location, typeof(NativeFileLease).Assembly.Location]));
        var compilation = CSharpCompilation.Create("Romd.Infrastructure.Tests", [CSharpSyntaxTree.ParseText(Program)], references.References,
            new CSharpCompilationOptions(OutputKind.ConsoleApplication, nullableContextOptions: NullableContextOptions.Enable));
        var emitted = compilation.Emit(_assembly);
        emitted.Success.ShouldBeTrue(string.Join("\n", emitted.Diagnostics));
        File.Copy(Path.ChangeExtension(typeof(PosixMediaToolFixture).Assembly.Location, ".runtimeconfig.json"),
            Path.ChangeExtension(_assembly, ".runtimeconfig.json"));
    }

    internal ProcessStartInfo Start(string mode, params string[] arguments)
    {
        var start = new ProcessStartInfo(new MediaProcessingOptions().DotnetPath) { UseShellExecute = false };
        foreach (string argument in new[] { _assembly, AppContext.BaseDirectory, mode }.Concat(arguments))
            start.ArgumentList.Add(argument);
        start.Environment.Clear();
        start.Environment["ROMD_POSIX_TOOL_FIXTURE"] = "explicit value";
        // macOS .NET needs TMPDIR for named mutexes used by covered payloads.
        if (OperatingSystem.IsMacOS())
            start.Environment["TMPDIR"] = TemporaryDirectory;
        return start;
    }

    private const string Program = """
        using System;
        using System.Collections;
        using System.IO;
        using System.Linq;
        using System.Runtime.CompilerServices;
        using System.Runtime.Loader;
        using System.Text;
        using System.Text.Json;
        using System.Threading.Tasks;
        using Microsoft.Win32.SafeHandles;
        using Romd.Infrastructure.Storage;
        using Romd.Media.Processes;

        AssemblyLoadContext.Default.Resolving += (_, name) =>
        {
            string path = Path.Combine(args[0], name.Name + ".dll");
            return File.Exists(path) ? AssemblyLoadContext.Default.LoadFromAssemblyPath(path) : null;
        };
        return await Tool.RunAsync(args);

        static class Tool
        {
            [MethodImpl(MethodImplOptions.NoInlining)]
            public static async Task<int> RunAsync(string[] args)
            {
                if (args[1] == "stream-failure")
                {
                    string pending = args[2] + ".tmp";
                    await File.WriteAllTextAsync(pending, Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
                    File.Move(pending, args[2]);
                    await Task.Delay(System.Threading.Timeout.InfiniteTimeSpan);
                    return 71;
                }
                int leaseFlags = NativeFileLease.Fcntl(3, 1, 0);
                if (leaseFlags < 0) return 72;
                int statusFlags = NativeFileLease.Fcntl(3, 3, 0);
                int nonblocking = OperatingSystem.IsMacOS() ? 4 : 0x800;
                if (statusFlags < 0 || NativeFileLease.Fcntl(3, 4, statusFlags | nonblocking) < 0)
                    throw new IOException("Cannot update the tool's admission status flags.");
                statusFlags = NativeFileLease.Fcntl(3, 3, 0);
                if (statusFlags < 0) throw new IOException("Cannot observe the tool's admission status flags.");
                byte[] nonce = new byte[32];
                using (var lease = new FileStream(new SafeFileHandle(3, ownsHandle: false), FileAccess.Read, 1, isAsync: false))
                {
                    lease.Seek(0, SeekOrigin.Begin);
                    lease.ReadExactly(nonce);
                }
                var identity = MediaProcessIdentity.Read(Environment.ProcessId) ?? throw new IOException("No tool process identity.");
                var environment = Environment.GetEnvironmentVariables().Cast<DictionaryEntry>()
                    .ToDictionary(entry => (string)entry.Key, entry => (string?)entry.Value);
                Console.WriteLine(JsonSerializer.Serialize(new
                {
                    Pid = Environment.ProcessId, identity.GroupId, LeaseFlags = leaseFlags, StatusFlags = statusFlags,
                    Nonce = Encoding.ASCII.GetString(nonce), Arguments = args[2..], Environment = environment
                }));
                await Console.Out.FlushAsync();
                if (args[1] == "hold")
                {
                    byte[] input = new byte[1];
                    if (await Console.OpenStandardInput().ReadAsync(input) != 1) return 73;
                }
                return 37;
            }
        }
        """;

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
