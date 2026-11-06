using ErrorOr;

namespace Romd.Admin.Application.Source.Dat.Subscriptions;

/// <summary>Explicit time and prior failure count determine the persisted check schedule.</summary>
public static class DatSubscriptionCheckSchedule
{
    public static (int ConsecutiveFailures, DateTimeOffset NextCheckAt) Advance(int previousFailures, bool failed, DateTimeOffset now)
    {
        int failures = failed ? Math.Min(previousFailures + 1, 6) : 0;
        int hours = failures == 0 ? 24 : Math.Min(24, 1 << (failures - 1));
        return (failures, now.AddHours(hours));
    }
}

internal sealed record DatSubscriptionVerifiedDocument(string ActiveSha256, string CandidateSha256, bool Unchanged);

internal sealed record DatSubscriptionCheckBinding(int? SourceId, int PlatformId, string SystemId, string Name, DateTimeOffset CheckedAt);

internal static class DatSubscriptionCheckTransition
{
    internal static DatSubscriptionCheckValues Failed(DatSubscriptionCheckValues original,
        DatSubscriptionCheckBinding? binding, string message, DateTimeOffset checkedAt, DateTimeOffset scheduleAt)
    {
        var next = Bind(original, binding) with { State = "CheckFailed", Message = message, LastCheckedAt = checkedAt };
        return Schedule(next, scheduleAt);
    }

    internal static DatSubscriptionCheckValues Verified(DatSubscriptionCheckValues original,
        DatSubscriptionCheckBinding binding, DatSubscriptionVerifiedDocument preview, int fileId, bool hasActive, DateTimeOffset scheduleAt) =>
        Schedule(Bind(original, binding) with
        {
            CandidateFileId = fileId,
            CandidateSha256 = preview.CandidateSha256,
            ActiveSha256 = preview.ActiveSha256,
            JobId = null,
            Message = null,
            LastCheckedAt = binding.CheckedAt,
            State = preview.Unchanged ? "UpToDate" : hasActive ? "UpdateAvailable" : "ReadyToImport"
        }, scheduleAt);

    private static DatSubscriptionCheckValues Bind(DatSubscriptionCheckValues original, DatSubscriptionCheckBinding? binding) =>
        binding is null ? original : original with
        { DatSourceId = binding.SourceId, PlatformId = binding.PlatformId, SystemId = binding.SystemId, ExpectedName = binding.Name };

    private static DatSubscriptionCheckValues Schedule(DatSubscriptionCheckValues values, DateTimeOffset now)
    {
        var next = DatSubscriptionCheckSchedule.Advance(values.ConsecutiveCheckFailures, values.State == "CheckFailed", now);
        return values with { ConsecutiveCheckFailures = next.ConsecutiveFailures, NextCheckAt = next.NextCheckAt };
    }
}

internal static class DatSubscriptionEnrollmentReplayPolicy
{
    internal static ErrorOr<DatSubscriptionCheckSnapshot?> Resolve(IReadOnlyList<DatSubscriptionCheckSnapshot> rows, int? requestedSource, int? trackedSource)
    {
        if (rows.Count == 0)
            return (DatSubscriptionCheckSnapshot?)null;
        if (rows is not [var only])
            return DatEnrollmentErrors.Ambiguous();
        if (requestedSource is not null && (only.Values.DatSourceId is not null
            || trackedSource is int tracked && tracked != requestedSource))
            return DatEnrollmentErrors.Ambiguous();
        return only;
    }
}
