using ErrorOr;

namespace Romd.Application.Common.Errors;

/// <summary>
///     The error metadata through which an upstream's "try again at" reaches the HTTP answer as <c>Retry-After</c>.
///     Any error may carry it; the host reads it here without knowing which area raised the error.
/// </summary>
public static class RetryAfterMetadata
{
    private const string Key = "RetryAfterUtc";

    /// <summary>The metadata for an error raised with <paramref name="retryAfter" />, or none.</summary>
    public static Dictionary<string, object>? For(DateTimeOffset? retryAfter) =>
        retryAfter is { } due ? new Dictionary<string, object> { [Key] = due } : null;

    /// <summary>When the error said to try again, if it did.</summary>
    public static DateTimeOffset? Of(Error error) => error.Metadata?.GetValueOrDefault(Key) as DateTimeOffset?;
}
