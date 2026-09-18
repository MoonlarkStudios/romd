using Microsoft.EntityFrameworkCore;
using Romd.Persistence.Entities;

namespace Romd.Persistence;

internal static class ConfigurationSave
{
    // EF commits the version-checked update and interceptor-created evidence in one SaveChanges transaction.
    public static async Task<bool> TrySaveAsync(RomdDbContext db, object entity, CancellationToken ct)
    {
        var previousEvents = db.ChangeTracker.Entries<AdminAuditEventEntity>().Select(entry => entry.Entity).ToHashSet();
        try { await db.SaveChangesAsync(ct); return true; }
        catch (DbUpdateConcurrencyException ex) when (ex.Entries.All(entry => ReferenceEquals(entry.Entity, entity)))
        {
            db.Entry(entity).State = EntityState.Detached;
            foreach (var audit in db.ChangeTracker.Entries<AdminAuditEventEntity>().ToArray())
                if (audit.State == EntityState.Added && !previousEvents.Contains(audit.Entity)) audit.State = EntityState.Detached;
            return false;
        }
    }
}
