using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Romd.Persistence.Entities;

namespace Romd.Persistence.Search;

/// <summary>
///     Keeps <c>SearchDocument</c> in step with every tracked create or rename of a title or DAT
///     game. Set-based writes bypass interceptors, so any <c>ExecuteUpdate</c> that changes a
///     searchable name or description must assign the document explicitly with
///     <see cref="SearchDocuments.For"/>.
/// </summary>
public sealed class SearchDocumentInterceptor : SaveChangesInterceptor
{
    public override InterceptionResult<int> SavingChanges(
        DbContextEventData eventData,
        InterceptionResult<int> result)
    {
        Refresh(eventData.Context);
        return base.SavingChanges(eventData, result);
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        Refresh(eventData.Context);
        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    private static void Refresh(DbContext? context)
    {
        if (context is null)
        {
            return;
        }

        foreach (var entry in context.ChangeTracker.Entries()
                     .Where(entry => entry.State is EntityState.Added or EntityState.Modified))
        {
            switch (entry.Entity)
            {
                case TitleEntity title when IsSearchTextChanged(entry, nameof(title.Name), nameof(title.Description)):
                    title.SearchDocument = SearchDocuments.For(title.Name, title.Description);
                    break;
                case DatGameEntity game when IsSearchTextChanged(entry, nameof(game.Name), nameof(game.Description)):
                    game.SearchDocument = SearchDocuments.For(game.Name, game.Description);
                    break;
            }
        }
    }

    private static bool IsSearchTextChanged(EntityEntry entry, string nameProperty, string descriptionProperty) =>
        entry.State == EntityState.Added
        || entry.Property(nameProperty).IsModified
        || entry.Property(descriptionProperty).IsModified;
}
