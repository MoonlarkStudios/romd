using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OpenIddict.EntityFrameworkCore;
using Romd.Infrastructure.Identity;
using Romd.Persistence;
using Romd.PostgreSql.TestSupport;
using Shouldly;
using Xunit;
using Xunit.Abstractions;

namespace Romd.Hosting.IntegrationTests.Hosting;

public sealed class ServerInstanceIdentityProcessTests(ITestOutputHelper output) : IDisposable
{
    private const string TestJwtSecret = "server-instance-process-test-secret-at-least-32-characters";
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"romd-server-identity-process-tests-{Guid.NewGuid():N}");

    [Fact]
    public async Task SeparateConsumerHostProcesses_StartingTogether_ObserveOneCompleteIdentity()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        string dataDirectory = await PrepareIdentityFreshDataDirectoryAsync(timeout.Token);
        using var database = PostgreSqlTestDatabase.Create();
        await database.ProvisionAsync(timeout.Token);
        string gatePath = Path.Combine(_root, $"start-{Guid.NewGuid():N}.gate");
        ChildHost? first = null;
        ChildHost? second = null;

        try
        {
            first = StartGatedConsumerHost(
                dataDirectory,
                database.ConnectionString,
                gatePath,
                GetAvailablePort(),
                "consumer-1");
            second = StartGatedConsumerHost(
                dataDirectory,
                database.ConnectionString,
                gatePath,
                GetAvailablePort(),
                "consumer-2");
            await Task.WhenAll(
                WaitUntilGateReadyAsync(first, timeout.Token),
                WaitUntilGateReadyAsync(second, timeout.Token));
            await File.WriteAllTextAsync(gatePath, "release", timeout.Token);

            Guid[] observed = await Task.WhenAll(
                ReadIdentityWhenReadyAsync(first, timeout.Token),
                ReadIdentityWhenReadyAsync(second, timeout.Token));

            observed.Distinct().ShouldHaveSingleItem();

            string identityPath = ServerInstanceIdentity.ResolvePath(dataDirectory);
            byte[] rawBytes = await File.ReadAllBytesAsync(identityPath, timeout.Token);
            rawBytes.Length.ShouldBe(36);
            string rawIdentity = Encoding.ASCII.GetString(rawBytes);
            Guid.TryParseExact(rawIdentity, "D", out Guid persisted).ShouldBeTrue();
            persisted.ToString("D").ShouldBe(rawIdentity);
            persisted.ShouldBe(observed[0]);

            Directory.GetFiles(
                    Path.GetDirectoryName(identityPath)!,
                    "server-instance-id.*.tmp",
                    SearchOption.TopDirectoryOnly)
                .ShouldBeEmpty();
        }
        finally
        {
            try
            {
                await Task.WhenAll(
                    first is null ? Task.CompletedTask : StopAndCaptureAsync(first),
                    second is null ? Task.CompletedTask : StopAndCaptureAsync(second));
            }
            finally
            {
                if (first is not null)
                {
                    WriteRawLogs(first);
                }

                if (second is not null)
                {
                    WriteRawLogs(second);
                }
            }
        }

        AssertBoundedCleanup(first);
        AssertBoundedCleanup(second);
        first.Process.Dispose();
        second.Process.Dispose();
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private async Task<string> PrepareIdentityFreshDataDirectoryAsync(CancellationToken cancellationToken)
    {
        string dataDirectory = Path.Combine(_root, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dataDirectory);
        string webRoot = Path.Combine(dataDirectory, "wwwroot");
        Directory.CreateDirectory(webRoot);
        await File.WriteAllTextAsync(
            Path.Combine(webRoot, "index.html"),
            "<!doctype html><html></html>",
            cancellationToken);

        OpenIddictSigningKey.EnsureCreated(dataDirectory);

        File.Exists(ServerInstanceIdentity.ResolvePath(dataDirectory)).ShouldBeFalse();
        return dataDirectory;
    }

    private static ChildHost StartGatedConsumerHost(
        string dataDirectory,
        string connectionString,
        string gatePath,
        int port,
        string name)
    {
        string hostDll = Path.Combine(AppContext.BaseDirectory, "romd-consumer.dll");
        File.Exists(hostDll).ShouldBeTrue($"Expected consumer host at '{hostDll}'.");
        string dotnetHost = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH")
                            ?? "dotnet";

        var startInfo = new ProcessStartInfo(dotnetHost)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add(hostDll);
        string readyPath = $"{gatePath}.{name}.ready";
        startInfo.Environment["ROMD_CONSUMER_TEST_STARTUP_GATE"] = gatePath;
        startInfo.Environment["ROMD_CONSUMER_TEST_STARTUP_READY"] = readyPath;
        startInfo.Environment["DOTNET_ENVIRONMENT"] = "Testing";
        startInfo.Environment["ASPNETCORE_ENVIRONMENT"] = "Testing";
        startInfo.Environment["ASPNETCORE_URLS"] = $"http://127.0.0.1:{port}";
        startInfo.Environment["ASPNETCORE_WEBROOT"] = Path.Combine(dataDirectory, "wwwroot");
        startInfo.Environment["hostBuilder__reloadConfigOnChange"] = "false";
        startInfo.Environment["Romd__DataDirectory"] = dataDirectory;
        startInfo.Environment["Romd__JwtSecret"] = TestJwtSecret;
        startInfo.Environment[$"ConnectionStrings__{PostgreSqlConfiguration.RuntimeConnectionName}"] =
            connectionString;

        var process = new Process { StartInfo = startInfo };
        process.Start().ShouldBeTrue();
        return new ChildHost(
            name,
            port,
            readyPath,
            process,
            process.StandardOutput.ReadToEndAsync(),
            process.StandardError.ReadToEndAsync());
    }

    private static async Task WaitUntilGateReadyAsync(ChildHost child, CancellationToken cancellationToken)
    {
        while (!File.Exists(child.ReadyPath))
        {
            child.Process.HasExited.ShouldBeFalse($"{child.Name} exited before reaching its startup gate.");
            await Task.Delay(TimeSpan.FromMilliseconds(25), cancellationToken);
        }
    }

    private static Task SignalAsync(int processId, string signal, CancellationToken cancellationToken) =>
        RunBoundedCommandAsync(
            "/bin/kill",
            [signal, processId.ToString()],
            cancellationToken);

    private static async Task<Guid> ReadIdentityWhenReadyAsync(
        ChildHost child,
        CancellationToken cancellationToken)
    {
        using var client = new HttpClient
        {
            BaseAddress = new Uri($"http://127.0.0.1:{child.Port}"),
            Timeout = TimeSpan.FromSeconds(1)
        };

        while (true)
        {
            if (child.Process.HasExited)
            {
                string logs = await CaptureCompletedOutputAsync(child);
                throw new InvalidOperationException(
                    $"{child.Name} exited with code {child.Process.ExitCode} before readiness.\n{logs}");
            }

            try
            {
                using var response = await client.GetAsync("/api/server/identity", cancellationToken);
                if (response.StatusCode == HttpStatusCode.OK)
                {
                    await using Stream content = await response.Content.ReadAsStreamAsync(cancellationToken);
                    using JsonDocument document = await JsonDocument.ParseAsync(
                        content,
                        cancellationToken: cancellationToken);
                    JsonElement root = document.RootElement;
                    root.EnumerateObject().Select(property => property.Name)
                        .ShouldBe(["instanceId"], ignoreOrder: false);
                    string? instanceId = root.GetProperty("instanceId").GetString();
                    Guid.TryParseExact(instanceId, "D", out Guid parsed).ShouldBeTrue();
                    parsed.ToString("D").ShouldBe(instanceId);
                    child.ObservedHealthy = true;
                    return parsed;
                }
            }
            catch (Exception ex) when (
                ex is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
            {
                // The real host has not bound its loopback socket yet.
            }

            await Task.Delay(TimeSpan.FromMilliseconds(50), cancellationToken);
        }
    }

    private static async Task<string> RunBoundedCommandAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo(fileName)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };
        process.Start().ShouldBeTrue();
        Task<string> stdout = process.StandardOutput.ReadToEndAsync();
        Task<string> stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync(cancellationToken);
        string output = await stdout.WaitAsync(cancellationToken);
        string error = await stderr.WaitAsync(cancellationToken);
        process.ExitCode.ShouldBe(0, $"{fileName} failed: {error}");
        return output;
    }

    private static async Task StopAndCaptureAsync(ChildHost child)
    {
        if (OperatingSystem.IsWindows())
        {
            if (!child.Process.HasExited)
            {
                child.Process.Kill(entireProcessTree: true);
                child.WasForceKilled = true;
            }
        }
        else
        {
            if (!child.Process.HasExited)
            {
                try
                {
                    using var signalTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                    await SignalAsync(child.Process.Id, "-TERM", signalTimeout.Token);
                }
                catch (Exception)
                {
                    // The process may have exited between the check and signal.
                }
            }

            if (!child.Process.HasExited)
            {
                using var gracefulTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                try
                {
                    await child.Process.WaitForExitAsync(gracefulTimeout.Token);
                }
                catch (OperationCanceledException)
                {
                    child.Process.Kill(entireProcessTree: true);
                    child.WasForceKilled = true;
                }
            }
        }

        if (!child.Process.HasExited)
        {
            using var killTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await child.Process.WaitForExitAsync(killTimeout.Token);
        }

        child.StandardOutput = await child.StandardOutputTask.WaitAsync(TimeSpan.FromSeconds(5));
        child.StandardError = await child.StandardErrorTask.WaitAsync(TimeSpan.FromSeconds(5));
    }

    private static void AssertBoundedCleanup(ChildHost child)
    {
        child.ObservedHealthy.ShouldBeTrue($"{child.Name} never served a valid identity response.");
        child.Process.HasExited.ShouldBeTrue($"{child.Name} survived bounded cleanup.");

        if (OperatingSystem.IsWindows())
        {
            child.WasForceKilled.ShouldBeTrue(
                $"{child.Name} did not complete the expected bounded Windows process-tree cleanup.");
            return;
        }

        child.WasForceKilled.ShouldBeFalse(
            $"{child.Name} did not terminate gracefully.\n{child.AllOutput}");
        child.Process.ExitCode.ShouldBe(0, $"{child.Name} did not terminate cleanly.\n{child.AllOutput}");
    }

    private static async Task<string> CaptureCompletedOutputAsync(ChildHost child)
    {
        string stdout = await child.StandardOutputTask.WaitAsync(TimeSpan.FromSeconds(5));
        string stderr = await child.StandardErrorTask.WaitAsync(TimeSpan.FromSeconds(5));
        return $"stdout:\n{stdout}\nstderr:\n{stderr}";
    }

    private void WriteRawLogs(ChildHost child)
    {
        output.WriteLine($"----- {child.Name} stdout -----");
        output.WriteLine(child.StandardOutput);
        output.WriteLine($"----- {child.Name} stderr -----");
        output.WriteLine(child.StandardError);
    }

    private static int GetAvailablePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private sealed class ChildHost(
        string name,
        int port,
        string readyPath,
        Process process,
        Task<string> standardOutputTask,
        Task<string> standardErrorTask)
    {
        public string Name { get; } = name;
        public int Port { get; } = port;
        public string ReadyPath { get; } = readyPath;
        public Process Process { get; } = process;
        public Task<string> StandardOutputTask { get; } = standardOutputTask;
        public Task<string> StandardErrorTask { get; } = standardErrorTask;
        public string StandardOutput { get; set; } = string.Empty;
        public string StandardError { get; set; } = string.Empty;
        public bool ObservedHealthy { get; set; }
        public bool WasForceKilled { get; set; }
        public string AllOutput => $"stdout:\n{StandardOutput}\nstderr:\n{StandardError}";
    }
}
