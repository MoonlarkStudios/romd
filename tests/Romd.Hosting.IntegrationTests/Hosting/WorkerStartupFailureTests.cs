using Shouldly;
using Xunit;

namespace Romd.Hosting.IntegrationTests.Hosting;

public sealed class WorkerStartupFailureTests
{
    [Fact]
    public async Task Worker_InvalidNativeAssets_ExitsWithFailureInsteadOfUnhandledRuntimeAbort()
    {
        string data = RomdHostProcesses.CreateDataDirectory("romd-worker-fatal-exit");
        const string connection = "Host=127.0.0.1;Port=1;Database=unused;Username=unused;Password=unused;Timeout=1";
        using var process = RomdHostProcesses.StartWorker(data, connection, connection,
            [("Romd__Native__LibchdrDirectory", Path.Combine(data, "missing-native")),
             ("Romd__Native__ChdmanDirectory", Path.Combine(data, "missing-tool"))]);
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await process.WaitForExitAsync(deadline.Token);
            process.WaitForExit(); // Join redirected output before checking diagnostics.

            process.ExitCode.ShouldBe(1, RomdHostProcesses.RecentOutput(process));
            RomdHostProcesses.RecentOutput(process).ShouldContain("Worker terminated unexpectedly");
            RomdHostProcesses.RecentOutput(process).ShouldContain("missing-native");
        }
        finally
        {
            RomdHostProcesses.Stop(process);
            Directory.Delete(data, recursive: true);
        }
    }
}
