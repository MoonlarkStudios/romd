using System.Reflection;
using System.Text.Json;
using ErrorOr;
using Microsoft.EntityFrameworkCore;
using Romd.Domain.ReferenceData;

namespace Romd.Persistence.ReferenceData;

internal static class ReferenceEditing
{
    internal static bool Label(string? value, int max) => value is { Length: > 0 } && value.Length <= max && value.Trim() == value && !value.Any(char.IsControl);
    internal static async Task<bool> HasDependentsAsync(RomdDbContext db, object projection, CancellationToken ct)
    {
        // Inspect every mapped FK, including relationships added by future features. Never allow
        // database cascade/set-null behavior to erase unrelated catalog data implicitly.
        var entry = db.Entry(projection);
        var id = (int)entry.Property("Id").CurrentValue!;
        foreach (var foreignKey in entry.Metadata.GetReferencingForeignKeys())
        {
            if (foreignKey.Properties.Count != 1) return true;
            var method = typeof(ReferenceEditing).GetMethod(nameof(HasDependentAsync), BindingFlags.Static | BindingFlags.NonPublic)!
                .MakeGenericMethod(foreignKey.DeclaringEntityType.ClrType);
            if (await (Task<bool>)method.Invoke(null, [db, foreignKey.Properties[0].Name, id, ct])!) return true;
        }
        return false;
    }
    private static Task<bool> HasDependentAsync<TEntity>(RomdDbContext db, string property, int id, CancellationToken ct) where TEntity : class =>
        db.Set<TEntity>().AnyAsync(entity => EF.Property<int?>(entity, property) == id, ct);
}
