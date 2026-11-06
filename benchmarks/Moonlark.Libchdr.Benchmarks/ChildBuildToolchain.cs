using BenchmarkDotNet.Characteristics;
using BenchmarkDotNet.Loggers;
using BenchmarkDotNet.Running;
using BenchmarkDotNet.Toolchains;
using BenchmarkDotNet.Toolchains.CsProj;
using BenchmarkDotNet.Toolchains.Parameters;
using BenchmarkDotNet.Toolchains.Results;
using BenchmarkDotNet.Validators;

namespace Moonlark.Libchdr.Benchmarks;

/// <summary>Retains BDN's normal build and execution while binding the actual relocated child outputs in the parent.</summary>
internal sealed class ChildBuildToolchain(string root, string inputs, string inputsHash) : IToolchain, IBuilder, IExecutor
{
    private static readonly object ExecutionEnvironmentLock = new();
    private readonly IToolchain _inner = CsProjCoreToolchain.NetCoreApp10_0;
    private readonly List<ChildBuildBinding> _bindings = [];
    internal const string ChildHashVariable = "MOONLARK_BENCHMARK_CHILD_SHA256";

    public string Name => _inner.Name;
    public IGenerator Generator => _inner.Generator;
    public IBuilder Builder => this;
    public IExecutor Executor => this;
    public bool IsInProcess => _inner.IsInProcess;
    public IEnumerable<ValidationError> Validate(BenchmarkCase benchmarkCase, IResolver resolver) => _inner.Validate(benchmarkCase, resolver);

    internal ChildBuildBinding[] Bindings { get { lock (_bindings) return _bindings.OrderBy(binding => binding.Partition, StringComparer.Ordinal).ToArray(); } }

    public BuildResult Build(GenerateResult generateResult, BuildPartition buildPartition, ILogger logger)
    {
        try
        {
            VerifyParent();
            BuildResult result = _inner.Builder.Build(generateResult, buildPartition, logger);
            VerifyParent();
            if (!result.IsBuildSuccess)
                return result;
            ArtifactsPaths paths = result.ArtifactsPaths;
            ChildBuildBinding binding = ChildBuildInputs.Capture(root, inputs, inputsHash,
                paths.BuildArtifactsDirectoryPath, paths.BinariesDirectoryPath, buildPartition.ProgramName,
                paths.ProjectFilePath, paths.BuildScriptFilePath, paths.ProgramCodePath);
            lock (_bindings)
                _bindings.Add(binding);
            return result;
        }
        catch (Exception exception) { return BuildResult.Failure(generateResult, exception); }
    }

    public ExecuteResult Execute(ExecuteParameters executeParameters)
    {
        ChildBuildBinding binding = Bindings.Single(item => item.BinariesDirectory == Path.GetFullPath(executeParameters.BuildResult.ArtifactsPaths.BinariesDirectoryPath));
        return WithChildReceipt(binding.Sha256, () =>
        {
            VerifyParent();
            ChildBuildInputs.VerifyCompleted(root, inputs, inputsHash, binding);
            try
            { return _inner.Executor.Execute(executeParameters); }
            finally
            {
                VerifyParent();
                ChildBuildInputs.VerifyCompleted(root, inputs, inputsHash, binding);
            }
        });
    }

    internal static T WithChildReceipt<T>(string hash, Func<T> execute)
    {
        lock (ExecutionEnvironmentLock)
        {
            string? previous = Environment.GetEnvironmentVariable(ChildHashVariable);
            try
            { Environment.SetEnvironmentVariable(ChildHashVariable, hash); return execute(); }
            finally { Environment.SetEnvironmentVariable(ChildHashVariable, previous); }
        }
    }

    internal void VerifyCompleted()
    {
        VerifyParent();
        ChildBuildBinding[] bindings = Bindings;
        if (bindings.Length == 0)
            throw new InvalidDataException("No completed child build receipt was captured.");
        foreach (ChildBuildBinding binding in bindings)
            ChildBuildInputs.VerifyCompleted(root, inputs, inputsHash, binding);
    }

    private void VerifyParent()
    {
        if (RunInputs.Hash(inputs) != inputsHash)
            throw new InvalidDataException("Original parent receipt changed.");
        RunInputs.Verify(root, inputs);
        RunInputs.VerifySource(root, inputs);
    }
}
