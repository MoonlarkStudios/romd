using Microsoft.EntityFrameworkCore;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Persistence;

public sealed partial class CatalogCdLayoutBoundaryTests
{
    [Theory]
    [InlineData("release-fingerprint")]
    [InlineData("requirement-fingerprint")]
    [InlineData("cue-digest")]
    [InlineData("bin-digest")]
    [InlineData("bin-name")]
    [InlineData("extra-sidecar")]
    [InlineData("title-conflict")]
    [InlineData("disk-requirement")]
    [InlineData("withdrawn-bin")]
    public async Task ReadCurrent_CompleteCatalogOrOwnerChanges_RefusesTheSourceWithoutRewritingProvenance(string mutation)
    {
        var (graph, id, revision) = await RetainAsync(preferred: true, ObservedContainer(canonical: false));
        await using var db = database.CreateContext();
        await using var transaction = await db.Database.BeginTransactionAsync();
        (await db.Database.SqlQuery<int>($"""SELECT "Id" AS "Value" FROM romd."CurrentCatalogCdLayoutBindings" WHERE "CatalogFileId"=2""").SingleAsync()).ShouldBe(id);
        if (mutation == "withdrawn-bin")
            await db.RomFiles.ExecuteDeleteAsync();
        else
            await MutateAsync(db, mutation);
        (await db.Database.SqlQuery<int>($"""SELECT "Id" AS "Value" FROM romd."CurrentCatalogCdLayoutBindings" WHERE "CatalogFileId"=2""").ToArrayAsync()).ShouldBeEmpty();
        (await db.Database.SqlQuery<Guid>($"""SELECT "Revision" AS "Value" FROM romd."CatalogCdLayoutBindings" WHERE "Id"={id}""").SingleAsync()).ShouldBe(revision);
        (await CountAsync(db, "Verifications")).ShouldBe(1);
        (await Romd.Persistence.MediaFacts.MediaFactLockSet.ReadAsync(db, [graph.Id], default)).ShouldContainKey(graph.Id);
        await transaction.RollbackAsync();
    }
}
