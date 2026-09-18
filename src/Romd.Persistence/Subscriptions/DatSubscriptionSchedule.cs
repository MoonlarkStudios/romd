using Romd.Persistence.Entities;

namespace Romd.Persistence.Subscriptions;

internal static class DatSubscriptionSchedule
{
    internal static void Advance(DatSubscriptionEntity row, DateTimeOffset now)
    {
        row.ConsecutiveCheckFailures = row.State == "CheckFailed" ? Math.Min(row.ConsecutiveCheckFailures + 1, 6) : 0;
        var hours = row.ConsecutiveCheckFailures == 0 ? 24 : Math.Min(24, 1 << (row.ConsecutiveCheckFailures - 1));
        row.NextCheckAt = now.AddHours(hours);
    }
}
