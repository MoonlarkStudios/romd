using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Romd.Persistence.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Romd.Application.Common.Security;
using Romd.Persistence.Entities;

namespace Romd.Persistence;

public sealed class AuditInterceptor(IAuditContext auditContext, TimeProvider timeProvider, bool captureAdministrativeChanges = true) : SaveChangesInterceptor
{
    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        if (eventData.Context is null)
            return base.SavingChangesAsync(eventData, result, cancellationToken);

        var now = timeProvider.GetUtcNow();
        var actorId = auditContext.ActorId;

        foreach (var entry in eventData.Context.ChangeTracker.Entries<ICreatableEntity>())
        {
            if (entry.State == EntityState.Added)
            {
                entry.Entity.CreatedAt = now;
                entry.Entity.CreatedByUserId = actorId;
            }

            // Prevent mutation of creation fields on update
            if (entry.State == EntityState.Modified)
            {
                entry.Property(nameof(ICreatableEntity.CreatedAt)).IsModified = false;
                entry.Property(nameof(ICreatableEntity.CreatedByUserId)).IsModified = false;
            }
        }

        foreach (var entry in eventData.Context.ChangeTracker.Entries<IEditableEntity>())
        {
            switch (entry.State)
            {
                case EntityState.Added:
                case EntityState.Modified:
                    entry.Entity.UpdatedAt = now;
                    entry.Entity.UpdatedByUserId = actorId;
                    break;
            }
        }

        if (!captureAdministrativeChanges)
            return base.SavingChangesAsync(eventData, result, cancellationToken);

        foreach (var entry in eventData.Context.ChangeTracker.Entries<AdminAuditEventEntity>())
        {
            if (entry.State != EntityState.Added) continue;
            if (entry.Entity.ActorId == Guid.Empty) entry.Entity.ActorId = actorId;
            if (entry.Entity.OccurredAt == default) entry.Entity.OccurredAt = now;
        }

        // Only explicit safe fields enter durable history. Credential values and tokens never do.
        var evidence = new List<AdminAuditEventEntity>();
        foreach (var entry in eventData.Context.ChangeTracker.Entries().ToArray())
        {
            if (entry.State is not (EntityState.Added or EntityState.Modified or EntityState.Deleted)) continue;
            (string Type, string Id, string[] Fields)? target = entry.Entity switch
            {
                RomdUser user => ("User", user.Id.ToString(), ["Email", "LibraryId", "IsSuspended", "RequiresActivation"]),
                IdentityUserRole<Guid> role => ("User", role.UserId.ToString(), ["RoleId"]),
                PlatformFieldDefaultEntity policy => ("System metadata", policy.PlatformId.ToString(), ["FieldName", "SourceId"]),
                MetadataProviderSettingsEntity provider => ("Integration", provider.ProviderId, ["Enabled", "ClientId"]),
                ArtworkEnrichmentSettingsEntity => ("Automation", "artwork", ["FillPosters", "FillHeroes", "FillLogos", "FillBackdrops", "ReviewBackdrops"]),
                RegionAliasEntity alias => ("Region", alias.RegionId.ToString(), ["NormalizedAlias"]),
                GameLanguageAliasEntity alias => ("Language", alias.GameLanguageId.ToString(), ["NormalizedAlias"]),
                RegionEntity region => ("Region", region.Id.ToString(), ["Name", "Code"]),
                GameLanguageEntity language => ("Language", language.Id.ToString(), ["Name", "Code"]),
                _ => null
            };
            if (target is not { } selected) continue;
            var changes = new Dictionary<string, object?>();
            foreach (var property in entry.Properties.Where(property => selected.Fields.Contains(property.Metadata.Name)))
            {
                if (entry.State == EntityState.Modified && !property.IsModified) continue;
                changes[property.Metadata.Name] = new
                {
                    Before = entry.State == EntityState.Added ? null : property.OriginalValue,
                    After = entry.State == EntityState.Deleted ? null : property.CurrentValue
                };
            }
            foreach (var credential in new[] { "PasswordHash", "ProtectedClientSecret" })
            {
                var property = entry.Properties.FirstOrDefault(property => property.Metadata.Name == credential);
                if (property is not null && (property.IsModified || entry.State == EntityState.Added))
                    changes[credential == "PasswordHash" ? "PasswordChanged" : "CredentialsChanged"] = true;
            }
            if (changes.Count == 0 && entry.State == EntityState.Modified) continue;
            evidence.Add(new AdminAuditEventEntity
            {
                ActorId = actorId, OccurredAt = now, TargetType = selected.Type, TargetId = selected.Id,
                Action = entry.State.ToString(), Changes = JsonSerializer.Serialize(changes)
            });
        }
        eventData.Context.Set<AdminAuditEventEntity>().AddRange(evidence);

        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }
}
