namespace Romd.Admin.Application.Ingestion.Jobs;

public sealed class JobExecutionOwnershipLostException : Exception
{
    public JobExecutionOwnershipLostException()
        : base("The job execution lease is no longer owned by this attempt.")
    {
    }
}
