using System.Collections.Immutable;
using Moonlark.Native.Engineering.Core;
using Moonlark.Native.Engineering.Generation;
using Moonlark.Native.Engineering.Tests.TestSupport;

namespace Moonlark.Native.Engineering.Tests.Generation;

/// <summary>A temp root holding a shared local clone of the real pinned upstream (no network) and copies of every generation input.</summary>
internal sealed class GenerationFixture : IDisposable
{
    private readonly TemporaryDirectory _directory = new();

    internal GenerationFixture()
    {
        Root = _directory.Path;
        Source = Path.Combine(Root, GenerationConfiguration.SourcePath);
        Directory.CreateDirectory(Path.GetDirectoryName(Source)!);
        _ = ProcessRunner.Run(["git", "clone", "--quiet", "--shared", "--no-checkout",
            Path.Combine(TestRepository.Root, GenerationConfiguration.SourcePath), Source], TestRepository.CleanEnvironment).Value;
        TestRepository.Git(Source, "checkout", "--quiet", "--detach", Authorities.ReadLibchdr(TestRepository.Root).Value.Pin.Commit);
        TestRepository.CopyTo(Root, Authorities.PinPath, Authorities.PropsPath, BindingGenerator.ToolManifestPath,
            GenerationConfiguration.EntryPath, GenerationConfiguration.ShimPath);
    }

    /// <summary>The committed raw ClangSharp output, which the pinned tool reproduces for the pinned headers.</summary>
    internal static string CommittedBindings { get; } = File.ReadAllText(Path.Combine(TestRepository.Root, BindingGenerator.BindingsPath));

    internal static string CommittedContract { get; } = File.ReadAllText(Path.Combine(TestRepository.Root, BindingGenerator.ContractPath));

    internal string Root { get; }

    internal string Source { get; }

    internal string Output => Path.Combine(Root, GenerationConfiguration.Libchdr.OutputDirectory);

    internal string Bindings => Path.Combine(Root, BindingGenerator.BindingsPath);

    internal string Contract => Path.Combine(Root, BindingGenerator.ContractPath);

    /// <summary>Every command the generator handed to the tool stub.</summary>
    internal List<IReadOnlyList<string>> Invocations { get; } = [];

    internal GeneratorTool Tool(Func<IReadOnlyList<string>, Failure?> behavior) => (command, _, _, _) =>
    {
        Invocations.Add(command);
        return behavior(command);
    };

    /// <summary>Runs the generator with a recording tool stub and no host arguments.</summary>
    internal Result<GenerationResult> Run(bool check, Func<IReadOnlyList<string>, Failure?> tool,
        IReadOnlyDictionary<string, string>? environment = null) =>
        BindingGenerator.Run(Root, "dotnet", check, environment ?? TestRepository.CleanEnvironment, Tool(tool), NoHostArguments);

    internal static Result<ImmutableArray<string>> NoHostArguments(IReadOnlyDictionary<string, string> environment, TextWriter log) =>
        ImmutableArray<string>.Empty;

    /// <summary>Simulates the tool: writes <paramref name="text"/> wherever the command's single --output points.</summary>
    internal Failure? WriteOutput(IReadOnlyList<string> command, string text)
    {
        int option = command.ToList().IndexOf("--output");
        File.WriteAllText(Path.Combine(Root, command[option + 1]), text);
        return null;
    }

    internal void CopyTrackedOutputs() => TestRepository.CopyTo(Root, BindingGenerator.BindingsPath, BindingGenerator.ContractPath);

    public void Dispose() => _directory.Dispose();
}
