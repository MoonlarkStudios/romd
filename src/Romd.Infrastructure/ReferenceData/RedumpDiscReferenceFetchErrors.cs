using ErrorOr;

namespace Romd.Infrastructure.ReferenceData;

public static class RedumpDiscReferenceFetchErrors
{
    public static Error InvalidDiscId() => Error.Validation("RedumpReferenceFetch.DiscId", "A positive Redump disc ID is required.");
    public static Error ResponseSource() => Error.Validation("RedumpReferenceFetch.Source", "The response did not retain the exact canonical requested source.");
    public static Error RefusedStatus() => Error.Validation("RedumpReferenceFetch.Status", "Redump did not return an accepted HTTP 200 response.");
    public static Error ContentType() => Error.Validation("RedumpReferenceFetch.ContentType", "The response must declare HTML and UTF-8 when a charset is given.");
    public static Error InputLimit() => Error.Validation("RedumpReferenceFetch.BodyLimit", "The complete reference body must contain at most 1 MiB and cannot be empty.");
    public static Error InvalidBody() => Error.Validation("RedumpReferenceFetch.Body", "The reference body has malformed headers, malformed or residual encoding, or an inconsistent declared length.");
    public static Error InvalidUtf8() => Error.Validation("RedumpReferenceFetch.Utf8", "The reference body is not strict UTF-8.");
    public static Error TimedOut() => Error.Failure("RedumpReferenceFetch.Timeout", "Redump retrieval timed out; the reference remains pending.");
    public static Error Unavailable() => Error.Failure("RedumpReferenceFetch.Unavailable", "Redump retrieval is temporarily unavailable; the reference remains pending.");
}
