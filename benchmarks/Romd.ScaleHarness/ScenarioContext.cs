using Microsoft.Extensions.DependencyInjection;

namespace Romd.ScaleHarness;

public sealed class ScenarioContext
{
    public ScenarioContext(
        ServiceProvider provider,
        SqlCaptureInterceptor sqlCapture,
        DeterministicDataset data,
        DatasetManifest manifest,
        string connectionString)
    {
        Provider = provider;
        SqlCapture = sqlCapture;
        Data = data;
        Manifest = manifest;
        ConnectionString = connectionString;
    }

    public ServiceProvider Provider { get; }
    public SqlCaptureInterceptor SqlCapture { get; }
    public DeterministicDataset Data { get; }
    public DatasetManifest Manifest { get; }
    public string ConnectionString { get; }
}

public interface IScenario
{
    string Name { get; }

    Task<ScenarioReport> RunAsync(ScenarioContext context, CancellationToken cancellationToken = default);
}
