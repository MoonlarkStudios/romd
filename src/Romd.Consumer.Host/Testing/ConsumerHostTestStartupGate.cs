using System.Globalization;
using Microsoft.Extensions.Hosting;

namespace Romd.Consumer.Host.Testing;

/// <summary>
///     Provides a bounded, explicitly activated rendezvous for real-host integration tests.
/// </summary>
internal static class ConsumerHostTestStartupGate
{
    internal const string GatePathEnvironmentVariable = "ROMD_CONSUMER_TEST_STARTUP_GATE";
    internal const string ReadyPathEnvironmentVariable = "ROMD_CONSUMER_TEST_STARTUP_READY";

    internal static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(30);

    internal static async Task WaitIfRequestedAsync(
        IHostEnvironment environment,
        Func<string, string?>? getEnvironmentVariable = null,
        TimeSpan? waitTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(environment);

        // Test-only settings must be completely inert in every deployable environment.
        if (!environment.IsEnvironment("Testing"))
        {
            return;
        }

        getEnvironmentVariable ??= Environment.GetEnvironmentVariable;
        string? gatePath = getEnvironmentVariable(GatePathEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(gatePath))
        {
            return;
        }

        string? readyPath = getEnvironmentVariable(ReadyPathEnvironmentVariable);
        string validatedGatePath = ValidatePath(GatePathEnvironmentVariable, gatePath);
        string validatedReadyPath = ValidatePath(ReadyPathEnvironmentVariable, readyPath);
        if (string.Equals(validatedGatePath, validatedReadyPath, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"{GatePathEnvironmentVariable} and {ReadyPathEnvironmentVariable} must use different paths.");
        }

        await File.WriteAllTextAsync(
            validatedReadyPath,
            Environment.ProcessId.ToString(CultureInfo.InvariantCulture));

        TimeSpan effectiveWaitTimeout = waitTimeout ?? WaitTimeout;
        if (effectiveWaitTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(waitTimeout), "The startup gate timeout must be positive.");
        }

        using var timeout = new CancellationTokenSource(effectiveWaitTimeout);
        while (!File.Exists(validatedGatePath))
        {
            await Task.Delay(TimeSpan.FromMilliseconds(10), timeout.Token);
        }
    }

    private static string ValidatePath(string variableName, string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new InvalidOperationException(
                $"{variableName} must be a non-empty absolute file path when the startup gate is activated.");
        }

        if (!Path.IsPathFullyQualified(path))
        {
            throw new InvalidOperationException($"{variableName} must be an absolute file path.");
        }

        string fullPath = Path.GetFullPath(path);
        string? parentDirectory = Path.GetDirectoryName(fullPath);
        if (parentDirectory is null || !Directory.Exists(parentDirectory))
        {
            throw new InvalidOperationException($"The parent directory for {variableName} must exist.");
        }

        if (Directory.Exists(fullPath))
        {
            throw new InvalidOperationException($"{variableName} must identify a file, not a directory.");
        }

        return fullPath;
    }
}
