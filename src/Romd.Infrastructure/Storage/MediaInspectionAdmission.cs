using ErrorOr;
using Romd.Admin.Application.Storage.Files;
using Romd.Media.Inspection;

namespace Romd.Infrastructure.Storage;

/// <summary>Single-use inspection capacity across source staging and stopped-child custody. Uncertain stop retains the charge.</summary>
internal sealed class MediaInspectionAdmission<T>(Func<ITempFile, CancellationToken, Task<(ErrorOr<T> Result, bool ProcessTreeStopped)>> run,
    MediaProcessingRuntime.MediaProcessingReservation lease, bool ownsLease) : IDisposable
{
    private int _state;
    public async Task<ErrorOr<T>> VerifyAsync(ITempFile source, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (Interlocked.CompareExchange(ref _state, 1, 0) != 0)
            throw new InvalidOperationException("An inspection reservation can be used only once.");
        bool stopped = false;
        using var active = lease.Activate();
        try
        {
            if (ct.IsCancellationRequested)
            {
                stopped = true; // No supervised operation has been invoked.
                ct.ThrowIfCancellationRequested();
            }
            var outcome = await run(source, ct);
            stopped = outcome.ProcessTreeStopped;
            ct.ThrowIfCancellationRequested();
            return outcome.Result;
        }
        catch (MediaWorkerStoppedCancellationException)
        {
            stopped = true;
            throw;
        }
        catch (Exception) when (!stopped)
        {
            try
            { source.Quarantine(); }
            catch (Exception) { }
            ct.ThrowIfCancellationRequested();
            return MediaInspectionErrors.Failed(MediaWorkerFailure.Containment);
        }
        finally
        {
            Interlocked.Exchange(ref _state, 3);
            if (!stopped)
                lease.Retain();
            else if (ownsLease)
                lease.Dispose();
        }
    }

    public void Dispose()
    {
        while (true)
        {
            int state = Volatile.Read(ref _state);
            if (state is 2 or 3)
                return;
            if (Interlocked.CompareExchange(ref _state, state == 0 ? 3 : 2, state) != state)
                continue;
            if (state == 0 && ownsLease)
                lease.Dispose();
            return;
        }
    }
}

