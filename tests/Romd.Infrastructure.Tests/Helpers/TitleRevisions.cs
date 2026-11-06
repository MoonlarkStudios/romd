using Microsoft.EntityFrameworkCore;
using Romd.Application.Common.Concurrency;
using Romd.Persistence;

namespace Romd.Infrastructure.Tests.Helpers;

internal static class TitleRevisions
{
    /// <summary>The title's stored revision, as a representation would expose it.</summary>
    public static async Task<string> RevisionAsync(RomdDbContext context, int titleId) =>
        (await context.Titles.AsNoTracking()
            .Where(title => title.Id == titleId)
            .Select(title => title.Revision)
            .SingleAsync()).ToString();

    /// <summary>The <c>If-Match</c> a client that just read the title would send.</summary>
    public static async Task<RevisionPrecondition> CurrentAsync(RomdDbContext context, int titleId) =>
        RevisionPrecondition.For(await RevisionAsync(context, titleId));
}
