namespace Romd.Admin.Application.Ingestion.Jobs;

/// <summary>Flows an executor's durable fence into command-owned transactions, including child scopes.</summary>
public sealed class JobExecutionFenceScope : IDisposable
{
    private static readonly AsyncLocal<(Guid JobId, Guid Token, TimeProvider Clock)?> Active = new();
    private readonly (Guid JobId, Guid Token, TimeProvider Clock)? _previous = Active.Value;
    public static (Guid JobId, Guid Token, TimeProvider Clock)? Current => Active.Value;

    public static JobExecutionFenceScope Enter(Guid jobId, Guid token, TimeProvider clock)
    {
        var scope = new JobExecutionFenceScope();
        Active.Value = (jobId, token, clock);
        return scope;
    }

    public void Dispose() => Active.Value = _previous;
}
