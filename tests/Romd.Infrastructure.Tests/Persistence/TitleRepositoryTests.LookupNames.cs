using Microsoft.Extensions.Logging.Abstractions;
using Romd.Admin.Application.Titles.Commands.ReplaceTitleLookupNames;
using Romd.Application.Common.Concurrency;
using Romd.Application.Common.Search;
using Romd.Persistence;
using Romd.Persistence.Entities;
using Romd.Persistence.Repositories;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Persistence;

public sealed partial class TitleRepositoryTests
{
    [Fact]
    public async Task ReplaceLookupNames_GroupedTitle_CanEditAndReduceNamesWithoutGrowing()
    {
        await using var db = CreateDb();
        await AddTitleAsync(db, id: 1, isTracked: false);
        string[] original = Enumerable.Range(1, 35).Select(number => $"Alias {number:D2}").ToArray();
        db.TitleLookupNames.AddRange(original.Select(name => new TitleLookupNameEntity
        {
            TitleId = 1,
            Name = name,
            SearchDocument = SearchTextNormalizer.Normalize(name)
        }));
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var store = new TitleLookupNameStore(db);
        var handler = new ReplaceTitleLookupNamesCommandHandler(store, new TitleRepository(db), new EfUnitOfWork(db),
            NullLogger<ReplaceTitleLookupNamesCommandHandler>.Instance);
        var before = (await store.ReadAsync(1, default)).ShouldNotBeNull();

        var tooMany = await handler.HandleAsync(new(1, [.. original, ""], RevisionPrecondition.For(before.Revision.ToString())));
        tooMany.FirstError.Code.ShouldBe("Titles.InvalidLookupNames");
        tooMany.FirstError.Description.ShouldBe("Supply at most 35 lookup names for this title.");
        var afterRejection = (await store.ReadAsync(1, default)).ShouldNotBeNull();
        afterRejection.Revision.ShouldBe(before.Revision);
        afterRejection.Names.ShouldBe(original);

        string[] renamed = [.. original[..^1], "Renamed alias"];
        var renamedResult = await handler.HandleAsync(new(1, renamed, RevisionPrecondition.For(before.Revision.ToString())));
        renamedResult.IsError.ShouldBeFalse();
        renamedResult.Value.Names.ShouldBe(renamed);
        renamedResult.Value.Revision.ShouldNotBe(before.Revision);

        var reduced = await handler.HandleAsync(new(1, renamed[1..], RevisionPrecondition.For(renamedResult.Value.Revision.ToString())));
        reduced.IsError.ShouldBeFalse();
        reduced.Value.Names.ShouldBe(renamed[1..]);
        reduced.Value.Revision.ShouldNotBe(renamedResult.Value.Revision);

        var stale = await handler.HandleAsync(new(1, renamed, RevisionPrecondition.For(renamedResult.Value.Revision.ToString())));
        stale.FirstError.Code.ShouldBe("Titles.PreconditionFailed");
        var regrow = await handler.HandleAsync(new(1, renamed, RevisionPrecondition.For(reduced.Value.Revision.ToString())));
        regrow.FirstError.Code.ShouldBe("Titles.InvalidLookupNames");
        var saved = (await store.ReadAsync(1, default)).ShouldNotBeNull();
        saved.Revision.ShouldBe(reduced.Value.Revision);
        saved.Names.ShouldBe(renamed[1..]);

        var capped = await handler.HandleAsync(new(1, renamed[3..], RevisionPrecondition.For(saved.Revision.ToString())));
        capped.IsError.ShouldBeFalse();
        capped.Value.Names.Count.ShouldBe(32);
        var aboveCap = await handler.HandleAsync(new(1, renamed[2..], RevisionPrecondition.For(capped.Value.Revision.ToString())));
        aboveCap.FirstError.Code.ShouldBe("Titles.InvalidLookupNames");
    }
}
