using System.Globalization;
using Microsoft.Extensions.Hosting;
using NSubstitute;
using Romd.Consumer.Host.Testing;
using Shouldly;
using Xunit;

namespace Romd.Hosting.IntegrationTests.Hosting;

public sealed class ConsumerHostTestStartupGateTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"romd-consumer-startup-gate-tests-{Guid.NewGuid():N}");

    [Fact]
    public async Task WaitIfRequestedAsync_NonTestingEnvironment_IgnoresAmbientSettings()
    {
        int environmentVariableReads = 0;

        await ConsumerHostTestStartupGate.WaitIfRequestedAsync(
            CreateEnvironment(Environments.Production),
            _ =>
            {
                environmentVariableReads++;
                return "malformed ambient value";
            });

        environmentVariableReads.ShouldBe(0);
    }

    [Fact]
    public async Task WaitIfRequestedAsync_TestingWithoutGatePath_IsInert()
    {
        int environmentVariableReads = 0;

        await ConsumerHostTestStartupGate.WaitIfRequestedAsync(
            CreateEnvironment("Testing"),
            _ =>
            {
                environmentVariableReads++;
                return null;
            });

        environmentVariableReads.ShouldBe(1);
    }

    [Fact]
    public async Task WaitIfRequestedAsync_ActivatedWithoutReadyPath_RejectsConfiguration()
    {
        Directory.CreateDirectory(_root);
        string gatePath = Path.Combine(_root, "start.gate");

        InvalidOperationException exception = await Should.ThrowAsync<InvalidOperationException>(() =>
            ConsumerHostTestStartupGate.WaitIfRequestedAsync(
                CreateEnvironment("Testing"),
                variable => variable == ConsumerHostTestStartupGate.GatePathEnvironmentVariable
                    ? gatePath
                    : null));

        exception.Message.ShouldContain(ConsumerHostTestStartupGate.ReadyPathEnvironmentVariable);
    }

    [Fact]
    public async Task WaitIfRequestedAsync_ActivatedWithRelativePath_RejectsConfiguration()
    {
        Directory.CreateDirectory(_root);
        string readyPath = Path.Combine(_root, "consumer.ready");

        InvalidOperationException exception = await Should.ThrowAsync<InvalidOperationException>(() =>
            ConsumerHostTestStartupGate.WaitIfRequestedAsync(
                CreateEnvironment("Testing"),
                CreateSettings("relative.gate", readyPath)));

        exception.Message.ShouldContain("absolute file path");
        File.Exists(readyPath).ShouldBeFalse();
    }

    [Fact]
    public async Task WaitIfRequestedAsync_ActivatedGate_WritesReadyFileAndReleases()
    {
        Directory.CreateDirectory(_root);
        string gatePath = Path.Combine(_root, "start.gate");
        string readyPath = Path.Combine(_root, "consumer.ready");

        Task wait = ConsumerHostTestStartupGate.WaitIfRequestedAsync(
            CreateEnvironment("Testing"),
            CreateSettings(gatePath, readyPath));
        await WaitUntilAsync(() => File.Exists(readyPath), TimeSpan.FromSeconds(2));

        string readyProcessId = await File.ReadAllTextAsync(readyPath);
        readyProcessId.ShouldBe(Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
        wait.IsCompleted.ShouldBeFalse();

        await File.WriteAllTextAsync(gatePath, "release");
        await wait.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task WaitIfRequestedAsync_UnreleasedGate_HasBoundedWait()
    {
        Directory.CreateDirectory(_root);
        string gatePath = Path.Combine(_root, "start.gate");
        string readyPath = Path.Combine(_root, "consumer.ready");

        await Should.ThrowAsync<OperationCanceledException>(() =>
            ConsumerHostTestStartupGate.WaitIfRequestedAsync(
                CreateEnvironment("Testing"),
                CreateSettings(gatePath, readyPath),
                TimeSpan.FromMilliseconds(100)));

        ConsumerHostTestStartupGate.WaitTimeout.ShouldBeLessThanOrEqualTo(TimeSpan.FromSeconds(30));
        File.Exists(readyPath).ShouldBeTrue();
        File.Exists(gatePath).ShouldBeFalse();
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private static IHostEnvironment CreateEnvironment(string name)
    {
        var environment = Substitute.For<IHostEnvironment>();
        environment.EnvironmentName.Returns(name);
        return environment;
    }

    private static Func<string, string?> CreateSettings(string gatePath, string readyPath) =>
        variable => variable switch
        {
            ConsumerHostTestStartupGate.GatePathEnvironmentVariable => gatePath,
            ConsumerHostTestStartupGate.ReadyPathEnvironmentVariable => readyPath,
            _ => null
        };

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        using var cancellation = new CancellationTokenSource(timeout);
        while (!condition())
        {
            await Task.Delay(TimeSpan.FromMilliseconds(10), cancellation.Token);
        }
    }
}
