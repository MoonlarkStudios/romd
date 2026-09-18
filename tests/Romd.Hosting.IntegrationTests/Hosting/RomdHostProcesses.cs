using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Romd.Infrastructure.Identity;
using Romd.Infrastructure.Jobs;
using Romd.Persistence;
using static Romd.Hosting.IntegrationTests.Hosting.HangfirePostgreSqlTestSupport;

namespace Romd.Hosting.IntegrationTests.Hosting;

/// <summary>
///     Starts the real host executables as separate processes, the way a deployment does, so
///     multi-process behaviour (provisioning ownership, fail-closed readiness, startup order) is
///     exercised on the actual entrypoints rather than on in-process test hosts.
/// </summary>
internal static class RomdHostProcesses
{
    private const int RetainedOutputLines = 80;
    private static readonly ConditionalWeakTable<Process, ConcurrentQueue<string>> Output = new();

    /// <summary>The most recent stdout/stderr lines of a started host, for failure messages.</summary>
    public static string RecentOutput(Process process) =>
        Output.TryGetValue(process, out ConcurrentQueue<string>? lines)
            ? string.Join(Environment.NewLine, lines)
            : string.Empty;

    public static string CreateDataDirectory(string prefix)
    {
        string dataDirectory = Path.Combine(Path.GetTempPath(), $"{prefix}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(dataDirectory, "content"));
        Directory.CreateDirectory(Path.Combine(dataDirectory, "wwwroot"));
        File.WriteAllText(Path.Combine(dataDirectory, "wwwroot", "index.html"), "<!doctype html>");
        OpenIddictSigningKey.EnsureCreated(dataDirectory);
        return dataDirectory;
    }

    public static Process StartAdmin(int port, string dataDirectory, string runtimeConnectionString) =>
        Start(
            HostAssembly("Romd.Admin.Host", "romd-admin.dll"),
            ["--urls", $"http://127.0.0.1:{port}"],
            dataDirectory,
            (HangfirePostgreSqlConfiguration.RuntimeConnectionName, runtimeConnectionString),
            (PostgreSqlConfiguration.RuntimeConnectionName, runtimeConnectionString));

    public static Process StartConsumer(int port, string dataDirectory, string runtimeConnectionString) =>
        Start(
            HostAssembly("Romd.Consumer.Host", "romd-consumer.dll"),
            ["--urls", $"http://127.0.0.1:{port}"],
            dataDirectory,
            (HangfirePostgreSqlConfiguration.RuntimeConnectionName, runtimeConnectionString),
            (PostgreSqlConfiguration.RuntimeConnectionName, runtimeConnectionString));

    /// <summary>The dedicated provisioning entrypoint used by the cutover runbook.</summary>
    public static Process StartProvisioner(string dataDirectory, string provisioningConnectionString) =>
        Start(
            HostAssembly("Romd.Worker.Host", "romd-worker.dll"),
            ["--provision-hangfire"],
            dataDirectory,
            (HangfirePostgreSqlConfiguration.ProvisioningConnectionName, provisioningConnectionString));

    /// <summary>The normal worker entrypoint: gate, provisioner, migrations, seeders, servers.</summary>
    public static Process StartWorker(
        string dataDirectory,
        string runtimeConnectionString,
        string provisioningConnectionString) =>
        Start(
            HostAssembly("Romd.Worker.Host", "romd-worker.dll"),
            [],
            dataDirectory,
            (HangfirePostgreSqlConfiguration.RuntimeConnectionName, runtimeConnectionString),
            (HangfirePostgreSqlConfiguration.ProvisioningConnectionName, provisioningConnectionString),
            (PostgreSqlConfiguration.RuntimeConnectionName, runtimeConnectionString),
            (PostgreSqlConfiguration.ProvisioningConnectionName, provisioningConnectionString));

    /// <summary>The dedicated application-schema entrypoint used by the cutover runbook.</summary>
    public static Process StartDatabaseProvisioner(string dataDirectory, string provisioningConnectionString) =>
        Start(
            HostAssembly("Romd.Worker.Host", "romd-worker.dll"),
            ["--migrate-database"],
            dataDirectory,
            (PostgreSqlConfiguration.ProvisioningConnectionName, provisioningConnectionString));

    public static async Task WaitForStatusAsync(
        HttpClient client,
        int port,
        string path,
        HttpStatusCode expected,
        TimeSpan? timeout = null)
    {
        using var budget = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(10));
        while (true)
        {
            try
            {
                using HttpResponseMessage response = await client.GetAsync(
                    $"http://127.0.0.1:{port}{path}", budget.Token);
                if (response.StatusCode == expected) return;
            }
            catch (HttpRequestException) when (!budget.IsCancellationRequested)
            {
            }
            catch (OperationCanceledException) when (!budget.IsCancellationRequested)
            {
                // The client's per-request timeout elapsed on a loaded runner; keep polling.
            }

            await Task.Delay(200, budget.Token);
        }
    }

    /// <summary>Waits until one named readiness check reports the expected per-check status.</summary>
    public static async Task WaitForReadinessCheckAsync(
        HttpClient client,
        int port,
        string checkName,
        string expectedStatus,
        TimeSpan timeout)
    {
        using var budget = new CancellationTokenSource(timeout);
        string? observed = null;
        while (true)
        {
            try
            {
                using HttpResponseMessage response = await client.GetAsync(
                    $"http://127.0.0.1:{port}/health/ready", budget.Token);
                using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(budget.Token));
                observed = body.RootElement.GetProperty("checks").EnumerateArray()
                    .SingleOrDefault(check => check.GetProperty("name").GetString() == checkName)
                    .TryGetProperty("status", out JsonElement status) ? status.GetString() : null;
                if (observed == expectedStatus) return;
            }
            catch (HttpRequestException) when (!budget.IsCancellationRequested)
            {
            }
            catch (OperationCanceledException) when (!budget.IsCancellationRequested)
            {
            }

            try
            {
                await Task.Delay(500, budget.Token);
            }
            catch (OperationCanceledException)
            {
                throw new TimeoutException(
                    $"Readiness check '{checkName}' on port {port} did not reach '{expectedStatus}'; last observed '{observed}'.");
            }
        }
    }

    public static int GetFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    public static void Stop(Process process)
    {
        if (process.HasExited) return;
        process.Kill(entireProcessTree: true);
        process.WaitForExit();
    }

    private static string HostAssembly(string project, string assembly) =>
        Path.Combine(RepositoryRoot, "src", project, "bin", BuildConfiguration, "net10.0", assembly);

    private static Process Start(
        string assembly,
        string[] arguments,
        string dataDirectory,
        params (string Name, string Value)[] connectionStrings)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = RepositoryRoot
        };
        start.ArgumentList.Add(assembly);
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        start.Environment["DOTNET_ENVIRONMENT"] = "Development";
        start.Environment["ASPNETCORE_ENVIRONMENT"] = "Development";
        start.Environment["Romd__DataDirectory"] = dataDirectory;
        start.Environment["Romd__JwtSecret"] = "cold-start-test-secret-at-least-32-characters";
        foreach ((string name, string value) in connectionStrings)
            start.Environment[$"ConnectionStrings__{name}"] = value;
        Process process = Process.Start(start) ?? throw new InvalidOperationException("Failed to start host process.");
        var lines = new ConcurrentQueue<string>();
        Output.Add(process, lines);
        DataReceivedEventHandler capture = (sender, received) =>
        {
            if (received.Data is null) return;
            lines.Enqueue(received.Data);
            while (lines.Count > RetainedOutputLines && lines.TryDequeue(out _))
            {
            }
        };
        process.OutputDataReceived += capture;
        process.ErrorDataReceived += capture;
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        return process;
    }
}
