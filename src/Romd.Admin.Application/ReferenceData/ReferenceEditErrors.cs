using ErrorOr;

namespace Romd.Admin.Application.ReferenceData;

internal static class ReferenceEditErrors
{
    internal static Error Invalid(string text) => Error.Validation("ReferenceResource.Invalid", text);
    internal static Error Missing() => Error.NotFound("ReferenceResource.NotFound", "Unknown reference identity.");
    internal static Error Conflict(string text) => Error.Conflict("ReferenceResource.Conflict", text);
    internal static Error Managed() => Error.Conflict("ReferenceResource.Managed", "ROMD owns this definition. Edit or reset its overrides instead.");
    internal static Error PreconditionRequired() => Error.Custom(428, "ReferenceResource.PreconditionRequired", "Send the resource ETag in If-Match.");
    internal static Error PreconditionFailed() => Error.Custom(412, "ReferenceResource.PreconditionFailed", "The resource changed. Reload it before editing.");
}
