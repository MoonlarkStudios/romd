using ErrorOr;

namespace Romd.Admin.Application.Source.Dat;

public static class DatEnrollmentErrors
{
    public static Error NotFound() => Error.NotFound("DatEnrollment.NotFound", "The subscription no longer exists.");
    public static Error Busy() => Error.Conflict("DatEnrollment.Busy", "A check or import is already running. Try again after it finishes.");
    public static Error Unavailable() => Error.Conflict("DatEnrollment.Unavailable", "This catalog is not currently available for subscription. Your installed catalogs are unchanged.");
    public static Error CheckFailed() => Error.Failure("DatEnrollment.CheckFailed", "The catalog could not be downloaded and verified. Your installed catalogs are unchanged. Retry the check.");
    public static Error Stale() => Error.Conflict("DatEnrollment.Stale", "The catalog changed after this review. Check for updates again before applying.");
    public static Error PlatformMissing() => Error.Conflict("DatEnrollment.PlatformMissing", "This publisher system key is unresolved. Register the system explicitly before subscribing; discovery never creates or reassigns reference identities.");
    public static Error Ambiguous() => Error.Conflict("DatEnrollment.Ambiguous", "More than one local source matches this catalog. Open the source you want to track from Systems.");
}
