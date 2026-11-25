using Microsoft.EntityFrameworkCore;
using Romd.Persistence.Entities;
using Romd.Persistence.Identity;
using Romd.Persistence.Repositories;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Persistence;

public sealed partial class TitleRepositoryTests
{
    [Fact]
    public async Task ReassignTitleReferencesAsync_Grouping_KeepsReleaseAndPlayerHistoryIdentities()
    {
        await using var db = CreateDb();
        await AddTitleAsync(db, id: 1, isTracked: false);
        await AddTitleAsync(db, id: 2, isTracked: false);
        var userId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();
        var startedAt = DateTimeOffset.UtcNow.AddMinutes(-5);
        db.Users.Add(new RomdUser { Id = userId, UserName = "grouping-player" });
        db.CatalogReleases.Add(new CatalogReleaseEntity
        {
            Id = 11,
            PlatformId = PlatformId,
            CatalogTitleId = 1,
            Fingerprint = "source-release",
            Name = "Original regional release"
        });
        db.PlaySessions.Add(new PlaySessionEntity
        {
            UserId = userId,
            SessionId = sessionId,
            ClientId = "grouping-test",
            TitleId = 1,
            ReleaseId = 11,
            StartedAt = startedAt,
            EndedAt = startedAt.AddMinutes(2),
            ActiveDurationSeconds = 120,
            CreatedAt = startedAt,
            UpdatedAt = startedAt
        });
        db.Collections.AddRange(
            new CollectionEntity { Id = 1, Name = "Regional editions" },
            new CollectionEntity { Id = 2, Name = "Shared shelf" });
        db.TitleLookupNames.AddRange(
            new TitleLookupNameEntity { TitleId = 1, Name = "Alias from source", SearchDocument = "alias from source" },
            new TitleLookupNameEntity { TitleId = 1, Name = "Shared alias", SearchDocument = "shared alias" },
            new TitleLookupNameEntity { TitleId = 2, Name = "Shared Alias", SearchDocument = "shared alias" });
        db.CollectionItems.AddRange(
            new CollectionItemEntity { Id = 31, CollectionId = 1, TitleId = 1, SortOrder = 6, Note = "Keep this edition", AddedAt = startedAt },
            new CollectionItemEntity { Id = 32, CollectionId = 2, TitleId = 1, SortOrder = 4, Note = "Add this note", AddedAt = startedAt },
            new CollectionItemEntity { Id = 33, CollectionId = 2, TitleId = 2, SortOrder = 8, AddedAt = startedAt });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var repository = new TitleRepository(db);
        await using (var transaction = await db.Database.BeginTransactionAsync())
        {
            await repository.ReassignTitleReferencesAsync(1, 2, default);
            await repository.DeleteAbsorbedSourceAsync(1);
            await transaction.CommitAsync();
        }

        (await db.Titles.AnyAsync(title => title.Id == 1)).ShouldBeFalse();
        (await db.CatalogReleases.SingleAsync(release => release.Id == 11)).CatalogTitleId.ShouldBe(2);
        var session = await db.PlaySessions.SingleAsync();
        session.SessionId.ShouldBe(sessionId);
        session.TitleId.ShouldBe(2);
        session.ReleaseId.ShouldBe(11);
        session.ActiveDurationSeconds.ShouldBe(120);
        var items = await db.CollectionItems.OrderBy(item => item.Id).ToArrayAsync();
        items.Select(item => item.Id).ShouldBe([31, 33]);
        items.ShouldAllBe(item => item.TitleId == 2);
        items[0].SortOrder.ShouldBe(6);
        items[0].Note.ShouldBe("Keep this edition");
        items[1].SortOrder.ShouldBe(8);
        items[1].Note.ShouldBe("Add this note");
        var lookupNames = await db.TitleLookupNames.OrderBy(name => name.SearchDocument).ToArrayAsync();
        lookupNames.Select(name => name.Name).ShouldBe(["Alias from source", "Shared Alias"]);
        lookupNames.ShouldAllBe(name => name.TitleId == 2);
    }
}
