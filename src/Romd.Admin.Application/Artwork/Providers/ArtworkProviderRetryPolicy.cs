using ErrorOr;

namespace Romd.Admin.Application.Artwork.Providers;

/// <summary>Bounds untrusted provider retry hints and keeps missing hints from causing a retry loop.</summary>
public static class ArtworkProviderRetryPolicy
{
    /// <summary>
    /// Uses the latest deadline within one failed operation, respecting every throttle it reports.
    /// Callers choosing between independent alternative providers may retry at the earliest provider deadline.
    /// </summary>
    public static DateTimeOffset? GetDeferredUntil(IEnumerable<Error> errors, DateTimeOffset now)
    {
        var deadlines = errors.Where(error => error.Code == "Artwork.ProviderRateLimited")
            .Select(error => ArtworkProviderErrors.GetRetryAfter(error) ?? now.AddMinutes(1)).ToArray();
        if (deadlines.Length == 0)
            return null;
        var due = deadlines.Max();
        return due < now.AddSeconds(1) ? now.AddSeconds(1) :
            due > now.AddHours(1) ? now.AddHours(1) : due;
    }
}
