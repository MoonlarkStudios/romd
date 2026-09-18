using Hangfire;
using Hangfire.PostgreSql;
using Hangfire.PostgreSql.Factories;
using Romd.Infrastructure.Jobs;

namespace Romd.Hosting.IntegrationTests.Hosting;

internal static class HangfirePostgreSqlTestSupport
{
    public static readonly string RepositoryRoot = FindRepositoryRoot();

    /// <summary>
    ///     Test output lives at tests/&lt;project&gt;/bin/&lt;Configuration&gt;/net10.0, and the host
    ///     and tool projects referenced by this project build into the same configuration name.
    /// </summary>
    public static readonly string BuildConfiguration = new DirectoryInfo(AppContext.BaseDirectory).Parent?.Name
        ?? throw new DirectoryNotFoundException("Test build configuration directory not found.");

    public static PostgreSqlStorage CreateRuntimeStorage(string connectionString)
    {
        var storageOptions = HangfirePostgreSqlConfiguration.CreateStorageOptions(prepareSchema: false);
        return new PostgreSqlStorage(
            new NpgsqlConnectionFactory(connectionString, storageOptions, null),
            storageOptions);
    }

    public static async Task WaitForLatestStateAsync(
        JobStorage storage,
        string jobId,
        string expected)
    {
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            while (true)
            {
                var details = storage.GetMonitoringApi().JobDetails(jobId);
                var latest = details?.History.FirstOrDefault();
                if (latest?.StateName == expected) return;
                if (latest?.StateName == "Failed")
                {
                    latest.Data.TryGetValue("ExceptionMessage", out string? exceptionMessage);
                    throw new InvalidOperationException(
                        $"Hangfire job {jobId} failed while waiting for {expected}: {exceptionMessage}");
                }
                await Task.Delay(100, budget.Token);
            }
        }
        catch (OperationCanceledException) when (budget.IsCancellationRequested)
        {
            string observed = string.Join(",", storage.GetMonitoringApi().JobDetails(jobId)?.History
                .Select(entry => entry.StateName) ?? []);
            throw new TimeoutException(
                $"Hangfire job {jobId} did not reach {expected}; observed history: {observed}");
        }
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Romd.sln")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("ROMD repository root not found.");
    }
}
