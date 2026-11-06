using Microsoft.EntityFrameworkCore;

namespace Romd.Persistence.Repositories;

internal static class TitleRowLock
{
    /// <summary>
    ///     Locks the title row until the caller's transaction ends. The no-op update leaves the
    ///     concurrency revision untouched, so staged title edits in the same transaction still apply.
    /// </summary>
    internal static async Task<bool> AcquireAsync(RomdDbContext context, int titleId, CancellationToken ct) =>
        await context.Titles.Where(title => title.Id == titleId).ExecuteUpdateAsync(setters =>
            setters.SetProperty(title => title.RetainWithoutCatalog, title => title.RetainWithoutCatalog), ct) == 1;
}
