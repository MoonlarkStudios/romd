using ErrorOr;

namespace Romd.Admin.Application.Ingestion.Jobs;

public static class JobErrors
{
    public static Error NotFound(Guid jobId) =>
        Error.NotFound("Jobs.NotFound", $"Job {jobId} was not found.");

    public static Error NotCancellable() =>
        Error.Conflict("Jobs.NotCancellable", "Metadata policy work cannot be cancelled.");

    public static Error AlreadyComplete() =>
        Error.Conflict("Jobs.AlreadyComplete", "Job is already complete.");

    public static Error InvalidCursor() => Error.Validation("Jobs.InvalidCursor", "The cursor is invalid for this query. Start from the first page.");
    public static Error FeedUnavailable() => Error.NotFound("Jobs.FeedUnavailable", "This job does not have that item feed.");

    public static Error NotTerminal() =>
        Error.Conflict("Jobs.NotTerminal", "Cannot archive an active job.");
}
