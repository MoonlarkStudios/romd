using System.Runtime.ExceptionServices;

namespace Romd.Admin.Application.Ingestion.Preparation;

/// <summary>Collects cleanup failures without owning resources or choosing their cleanup order.</summary>
internal sealed class JobAcceptanceCleanup(bool preserveOutcome, Action<Exception, string> reportFailure)
{
    private List<Exception>? _failures;

    public Task DisposeAsync(IAsyncDisposable? resource, string name) => resource is null
        ? Task.CompletedTask
        : RunAsync(() => resource.DisposeAsync().AsTask(), name);

    public void Dispose(IDisposable? resource, string name)
    {
        if (resource is null)
            return;
        try
        {
            resource.Dispose();
        }
        catch (Exception error)
        {
            Record(error, name);
        }
    }

    public async Task RunAsync(Func<Task> cleanup, string name)
    {
        try
        {
            await cleanup();
        }
        catch (Exception error)
        {
            Record(error, name);
        }
    }

    public void ThrowIfFailed(string message)
    {
        if (_failures is { Count: 1 })
            ExceptionDispatchInfo.Capture(_failures[0]).Throw();
        if (_failures is { Count: > 1 })
            throw new AggregateException(message, _failures);
    }

    private void Record(Exception error, string name)
    {
        if (preserveOutcome)
            reportFailure(error, name);
        else
            (_failures ??= []).Add(error);
    }
}
