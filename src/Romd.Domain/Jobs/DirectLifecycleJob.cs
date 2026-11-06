namespace Romd.Domain.Jobs;

/// <summary>
///     A job whose owner can complete or fail it directly. Phase guards still decide whether a
///     transition is allowed; journal-owned jobs do not expose these capabilities.
/// </summary>
public abstract class DirectLifecycleJob : Job
{
    /// <summary>
    ///     Completes the job successfully.
    /// </summary>
    public void Complete(DateTimeOffset completedAt)
    {
        ValidateCanComplete();
        SetTerminalPhase(HasErrors);
        CompletedAt = completedAt;
        CurrentItem = null;
    }

    /// <summary>
    ///     Fails the job with a reason.
    /// </summary>
    public void Fail(string reason, DateTimeOffset failedAt)
    {
        if (IsTerminal)
            return;
        SetFailedPhase();
        CompletedAt = failedAt;
        AddError("job", reason, failedAt);
    }

    /// <summary>Sets the terminal phase (Completed or CompletedWithErrors).</summary>
    protected abstract void SetTerminalPhase(bool hasErrors);

    /// <summary>Sets the Failed phase.</summary>
    protected abstract void SetFailedPhase();

    /// <summary>Validates the family's phase-specific completion rules.</summary>
    protected virtual void ValidateCanComplete() { }
}
