using ErrorOr;

namespace Romd.Admin.Application.Catalog.Sources;

public static class DatSourceLifecycleIntent
{
    public static bool IsValid(string? action) => action is "Delete" or "Disabled" or "Discontinued" or "Active";
    public static Error InvalidAction() => Error.Validation("SourceLifecycle.Action", "Choose a valid source action.");
    public static Error Missing() => Error.NotFound("SourceLifecycle.NotFound", "This source no longer exists. Refresh the system.");
    public static Error Busy() => Error.Conflict("SourceLifecycle.Busy", "A source check or import is running. Wait for it to finish, then review again.");
    public static Error Stale() => Error.Conflict("SourceLifecycle.Stale", "The source or its coverage changed. Review the updated impact before continuing.");
}
