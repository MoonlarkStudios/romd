using Romd.Persistence.Entities;
using Romd.Admin.Application.Source.Dat.Subscriptions;

namespace Romd.Persistence.Subscriptions;

internal static class DatSubscriptionSchedule
{
    internal static void Advance(DatSubscriptionEntity row, DateTimeOffset now)
    {
        var next = DatSubscriptionCheckSchedule.Advance(row.ConsecutiveCheckFailures, row.State == "CheckFailed", now);
        row.ConsecutiveCheckFailures = next.ConsecutiveFailures;
        row.NextCheckAt = next.NextCheckAt;
    }
}
