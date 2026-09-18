using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;
using OpenIddict.EntityFrameworkCore.Models;
using Romd.Persistence.Identity;
using Romd.Persistence.Entities;

namespace Romd.Persistence;

public sealed class ConsumerWriteGuardInterceptor : SaveChangesInterceptor
{
    private static readonly IReadOnlySet<string> ConsumerWritableUserProperties = new HashSet<string>
    {
        nameof(RomdUser.PasswordHash),
        nameof(RomdUser.SecurityStamp),
        nameof(RomdUser.LastSignedInAt),
        nameof(RomdUser.ConcurrencyStamp)
    };

    // The consumer host issues, rotates, and revokes tokens, so it must write authorization and
    // token rows. Application and scope rows are seeded by the worker and stay read-only here.
    private static readonly IReadOnlySet<EntityState> ConsumerWritableOpenIddictStates = new HashSet<EntityState>
    {
        EntityState.Added,
        EntityState.Modified,
        EntityState.Deleted
    };

    private static readonly IReadOnlyDictionary<Type, IReadOnlySet<EntityState>> ConsumerWritableEntityStates =
        new Dictionary<Type, IReadOnlySet<EntityState>>
        {
            [typeof(AccountSessionEntity)] = new HashSet<EntityState> { EntityState.Added, EntityState.Modified },
            [typeof(ConsumerUserSettingsEntity)] = new HashSet<EntityState>
            {
                EntityState.Added,
                EntityState.Modified
            },
            [typeof(PlaySessionEntity)] = new HashSet<EntityState>
            {
                EntityState.Added,
                EntityState.Modified,
                EntityState.Deleted
            },
            [typeof(OpenIddictEntityFrameworkCoreAuthorization)] = ConsumerWritableOpenIddictStates,
            [typeof(OpenIddictEntityFrameworkCoreToken)] = ConsumerWritableOpenIddictStates
        };

    public override InterceptionResult<int> SavingChanges(
        DbContextEventData eventData,
        InterceptionResult<int> result)
    {
        GuardConsumerWrites(eventData.Context, useAsync: false).GetAwaiter().GetResult();

        return base.SavingChanges(eventData, result);
    }

    public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        await GuardConsumerWrites(eventData.Context, useAsync: true, cancellationToken);

        return await base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    private static async ValueTask GuardConsumerWrites(
        DbContext? context,
        bool useAsync,
        CancellationToken cancellationToken = default)
    {
        if (context is null)
        {
            return;
        }

        foreach (var entry in context.ChangeTracker.Entries()
            .Where(entry => entry.State is EntityState.Added or EntityState.Modified or EntityState.Deleted)
            .OrderBy(entry => entry.Metadata.ClrType.Name, StringComparer.Ordinal))
        {
            string? forbiddenProperty = await GetForbiddenConsumerWritePropertyAsync(
                entry,
                useAsync,
                cancellationToken);

            if (forbiddenProperty is null)
            {
                continue;
            }

            string entityTypeName = entry.Metadata.ClrType.Name;
            string propertyMessage = forbiddenProperty.Length == 0
                ? string.Empty
                : $" property '{forbiddenProperty}'";
            throw new InvalidOperationException(
                $"Consumer persistence cannot write entity type '{entityTypeName}'{propertyMessage} in state '{entry.State}'. " +
                "Use admin persistence for catalog, curation, library, job, export, materialization, provider, and taxonomy writes.");
        }
    }

    private static async ValueTask<string?> GetForbiddenConsumerWritePropertyAsync(
        EntityEntry entry,
        bool useAsync,
        CancellationToken cancellationToken)
    {
        if (entry.Entity is RomdUser)
        {
            return await GetForbiddenUserWritePropertyAsync(entry, useAsync, cancellationToken);
        }

        return IsConsumerWritableEntry(entry.Metadata.ClrType, entry.State)
            ? null
            : string.Empty;
    }

    private static bool IsConsumerWritableEntry(Type entityType, EntityState state) =>
        ConsumerWritableEntityStates.TryGetValue(entityType, out var writableStates)
        && writableStates.Contains(state);

    private static async ValueTask<string?> GetForbiddenUserWritePropertyAsync(
        EntityEntry entry,
        bool useAsync,
        CancellationToken cancellationToken)
    {
        if (entry.State is not EntityState.Modified)
        {
            return string.Empty;
        }

        var databaseValues = useAsync
            ? await entry.GetDatabaseValuesAsync(cancellationToken)
            : entry.GetDatabaseValues();

        if (databaseValues is null)
        {
            return string.Empty;
        }

        return entry.Properties
            .Where(property => property.IsModified)
            .Where(property => !ValuesEqual(property.CurrentValue, databaseValues[property.Metadata.Name]))
            .Select(property => property.Metadata.Name)
            .FirstOrDefault(propertyName => !ConsumerWritableUserProperties.Contains(propertyName));
    }

    private static bool ValuesEqual(object? currentValue, object? databaseValue) =>
        currentValue switch
        {
            byte[] currentBytes when databaseValue is byte[] databaseBytes => currentBytes.SequenceEqual(databaseBytes),
            _ => Equals(currentValue, databaseValue)
        };
}
