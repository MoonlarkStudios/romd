using System.Net;
using Shouldly;
using Xunit;

namespace Romd.Hosting.IntegrationTests.Vfs;

public sealed class VfsAdmissionCleanupTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RunAsync_EarlierRequestFault_JoinsDelayedAndLaterRequestsAndPreservesPrimary(bool actionFails)
    {
        var primary = new ShouldAssertException("primary assertion");
        var transport = new HttpRequestException("request failed");
        var delayed = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var stop = new CancellationTokenSource();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var delayedBody = new DisposalProbeStream();
        using var laterBody = new DisposalProbeStream();
        var delayedResponse = new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = new StreamContent(delayedBody) };
        var laterResponse = new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = new StreamContent(laterBody) };
        var lease = new DisposalProbeLease();
        var diagnostics = new List<string>();
        var cleanup = VfsProbeEvidence.RunAsync(
            _ => actionFails ? Task.FromException<bool>(primary) : Task.FromResult(true),
            _ => Task.CompletedTask,
            _ => VfsAdmissionCleanup.RunAsync(stop, [lease],
                [Task.FromException<HttpResponseMessage>(transport),
                 Task.FromCanceled<HttpResponseMessage>(new CancellationToken(canceled: true)),
                 delayed.Task, Task.FromResult(laterResponse)]),
            diagnostics.Add, CancellationToken.None);
        try
        {
            stop.IsCancellationRequested.ShouldBeTrue();
            lease.Disposed.ShouldBeTrue();
            cleanup.IsCompleted.ShouldBeFalse("the earlier fault must not bypass the delayed request");
            delayedBody.Disposed.ShouldBeFalse();
            laterBody.Disposed.ShouldBeFalse("cleanup must still be waiting at the delayed request");
            delayed.SetResult(delayedResponse);
            Exception? observed = null;
            try
            { await cleanup.WaitAsync(deadline.Token); }
            catch (Exception error) { observed = error; }
            observed.ShouldBeSameAs(actionFails ? primary : transport);
            delayedBody.Disposed.ShouldBeTrue();
            laterBody.Disposed.ShouldBeTrue("cleanup must continue through requests after the delayed request");
            diagnostics.ShouldBe(["cleanup:System.Net.Http.HttpRequestException"]);
        }
        finally
        {
            delayed.TrySetResult(delayedResponse);
            try
            { await cleanup.WaitAsync(deadline.Token); }
            catch (Exception) when (cleanup.IsCompleted) { }
            delayedResponse.Dispose();
            laterResponse.Dispose();
        }
    }

    private sealed class DisposalProbeStream : MemoryStream
    {
        public bool Disposed { get; private set; }
        protected override void Dispose(bool disposing)
        {
            Disposed = true;
            base.Dispose(disposing);
        }
    }

    private sealed class DisposalProbeLease : IDisposable
    {
        public bool Disposed { get; private set; }
        public void Dispose() => Disposed = true;
    }
}
