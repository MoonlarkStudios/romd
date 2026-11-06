namespace Romd.Domain.Jobs;

/// <summary>A directly managed job whose owner may also apply cancellation.</summary>
public abstract class CancellableJob : DirectLifecycleJob
{
    /// <summary>
    ///     Cancels the job.
    /// </summary>
    public void Cancel(DateTimeOffset cancelledAt)
    {
        if (IsTerminal)
            return;
        SetCancelledPhase();
        CompletedAt = cancelledAt;
    }

    /// <summary>Sets the Cancelled phase.</summary>
    protected abstract void SetCancelledPhase();
}
