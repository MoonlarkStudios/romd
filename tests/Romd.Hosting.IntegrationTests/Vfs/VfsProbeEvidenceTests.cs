using Shouldly;
using Xunit;

namespace Romd.Hosting.IntegrationTests.Vfs;

public sealed class VfsProbeEvidenceTests
{
    [Fact]
    public async Task RunAsync_ReadAndWriterFail_PreservesPrimaryIdentityAndStackAndRunsCleanup()
    {
        var primary = new IOException("private-read-message");
        var secondary = new UnauthorizedAccessException("private-writer-message");
        var diagnostics = new List<string>();
        bool cleaned = false;

        var thrown = await Should.ThrowAsync<IOException>(async () => await VfsProbeEvidence.RunAsync(
            _ => ThrowPrimaryAsync(primary), _ => Task.FromException(secondary),
            _ => { cleaned = true; return Task.CompletedTask; }, diagnostics.Add, CancellationToken.None));

        thrown.ShouldBeSameAs(primary);
        thrown.StackTrace.ShouldNotBeNull().ShouldContain(nameof(ThrowPrimaryAsync));
        cleaned.ShouldBeTrue();
        diagnostics.ShouldBe(["evidence:System.UnauthorizedAccessException"]);
    }

    [Fact]
    public async Task RunAsync_CanceledReadAndWriterFail_PreservesCancellationAndUsesIndependentCleanupToken()
    {
        using var canceled = new CancellationTokenSource();
        await canceled.CancelAsync();
        var primary = new OperationCanceledException(canceled.Token);
        var diagnostics = new List<string>();
        bool cleaned = false;
        OperationCanceledException? thrown = null;
        try
        {
            await VfsProbeEvidence.RunAsync(
                token => { token.ShouldBe(canceled.Token); return Task.FromException<int>(primary); },
                token => { token.CanBeCanceled.ShouldBeFalse(); return Task.FromException(new IOException("private-writer-message")); },
                token => { token.CanBeCanceled.ShouldBeFalse(); cleaned = true; return Task.CompletedTask; },
                diagnostics.Add, canceled.Token);
        }
        catch (OperationCanceledException error) { thrown = error; }

        thrown.ShouldNotBeNull().ShouldBeSameAs(primary);
        cleaned.ShouldBeTrue();
        diagnostics.ShouldBe(["evidence:System.IO.IOException"]);
    }

    [Fact]
    public async Task RunAsync_WriterFailsAfterSuccessfulAction_ReportsFailureAndCleansBeforeRethrowingWriter()
    {
        var writer = new IOException("private-writer-message");
        bool cleaned = false;
        var diagnostics = new List<string>();
        var thrown = await Should.ThrowAsync<IOException>(async () => await VfsProbeEvidence.RunAsync(
            _ => Task.FromResult(41), _ => Task.FromException(writer),
            _ => { cleaned = true; return Task.CompletedTask; }, diagnostics.Add, CancellationToken.None));

        thrown.ShouldBeSameAs(writer);
        cleaned.ShouldBeTrue();
        diagnostics.ShouldBe(["evidence:System.IO.IOException"]);
    }

    [Fact]
    public async Task RunAsync_ReadAndCleanupFail_PreservesReadAndReportsSafeCleanupType()
    {
        var primary = new IOException("private-read-message");
        var diagnostics = new List<string>();
        var thrown = await Should.ThrowAsync<IOException>(async () => await VfsProbeEvidence.RunAsync(
            _ => ThrowPrimaryAsync(primary), _ => Task.CompletedTask,
            _ => Task.FromException(new InvalidOperationException("private-cleanup-message")),
            diagnostics.Add, CancellationToken.None));

        thrown.ShouldBeSameAs(primary);
        diagnostics.ShouldBe(["cleanup:System.InvalidOperationException"]);
    }

    [Fact]
    public async Task RunAsync_DiagnosticSinkAlsoFails_StillRunsCleanupAndPreservesPrimary()
    {
        var primary = new IOException("private-read-message");
        bool cleaned = false;
        var thrown = await Should.ThrowAsync<IOException>(async () => await VfsProbeEvidence.RunAsync(
            _ => ThrowPrimaryAsync(primary), _ => Task.FromException(new IOException("private-writer-message")),
            _ => { cleaned = true; return Task.CompletedTask; },
            _ => throw new InvalidOperationException("private-sink-message"), CancellationToken.None));

        thrown.ShouldBeSameAs(primary);
        cleaned.ShouldBeTrue();
        thrown.Data[VfsProbeEvidence.FailureTypesKey].ShouldBeOfType<string[]>().ShouldBe(
            ["evidence:System.IO.IOException", "diagnostic:System.InvalidOperationException"]);
    }

    [Fact]
    public async Task RunAsync_AllPhasesSucceed_ReturnsActionResultAndWritesBeforeCleanup()
    {
        var phases = new List<string>();
        int value = await VfsProbeEvidence.RunAsync(
            _ => { phases.Add("action"); return Task.FromResult(41); },
            _ => { phases.Add("evidence"); return Task.CompletedTask; },
            _ => { phases.Add("cleanup"); return Task.CompletedTask; },
            _ => throw new InvalidOperationException("Unexpected failure diagnostic"), CancellationToken.None);

        value.ShouldBe(41);
        phases.ShouldBe(["action", "evidence", "cleanup"]);
    }

    private static async Task<int> ThrowPrimaryAsync(Exception primary)
    {
        await Task.Yield();
        throw primary;
    }
}
