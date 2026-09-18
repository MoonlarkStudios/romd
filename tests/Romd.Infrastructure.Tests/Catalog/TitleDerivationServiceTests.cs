using ErrorOr;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Romd.Admin.Application.Catalog;
using Romd.Domain.Hashing;
using Romd.Infrastructure.Catalog;
using Romd.Persistence;
using Romd.Persistence.Entities;
using Romd.Persistence.Repositories;
using Romd.PostgreSql.TestSupport;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Catalog;

/// <summary>
///     Real-SQLite contract proofs for <see cref="TitleDerivationService" />: one assignment
///     per claim in claim order, entry identity upserted on (source, key), curation-preserving
///     links, BIOS/unrouted handling, platform stamping, reconcile deletion, run stamps, and
///     the flush protocol.
/// </summary>
public sealed class TitleDerivationServiceTests : IDisposable
{
    private const int PlatformId = 3;
    private const int OtherPlatformId = 4;
    private const int SourceId = 7;
    private const int OtherSourceId = 8;
    private const int DatFileId = 7;

    private readonly PostgreSqlTestDatabase _connection;
    private readonly DbContextOptions<RomdDbContext> _contextOptions;

    public TitleDerivationServiceTests()
    {
        _connection = PostgreSqlTestDatabase.Create();

        _contextOptions = new DbContextOptionsBuilder<RomdDbContext>()
            .UseNpgsql(_connection.ConnectionString)
            .Options;

        using var db = CreateDb();
        SeedCatalog(db);
    }

    [Fact]
    public async Task Reconcile_DuplicateEntryKeys_YieldsOneAssignmentPerClaimInClaimOrder()
    {
        await using var db = CreateDb();
        var service = CreateService(db);

        var assignments = await CollectAsync(service.ReconcileAsync(SourceId, Stream(
            Claim("Game A"),
            Claim("Game B"),
            Claim("Game A"))));

        assignments.Count.ShouldBe(3);
        assignments.ShouldAllBe(a => !a.IsError);
        var values = assignments.Select(a => a.Value).ToList();
        values.Select(v => v.EntryKey).ShouldBe(["Game A", "Game B", "Game A"]);

        // Duplicate keys resolve to the same entry; only the first creating claim reports it.
        values[2].SourceEntryId.ShouldBe(values[0].SourceEntryId);
        values[1].SourceEntryId.ShouldNotBe(values[0].SourceEntryId);
        values[0].TitleWasCreated.ShouldBeTrue();
        values[1].TitleWasCreated.ShouldBeTrue();
        values[2].TitleWasCreated.ShouldBeFalse();
        values[2].TitleId.ShouldBe(values[0].TitleId);

        // The reported title ids are real persisted rows linked to the entries.
        var links = await db.TitleSourceLinks.AsNoTracking()
            .ToDictionaryAsync(l => l.SourceEntryId, l => l.TitleId);
        links[values[0].SourceEntryId].ShouldBe(values[0].TitleId!.Value);
        links[values[1].SourceEntryId].ShouldBe(values[1].TitleId!.Value);
        (await db.Titles.CountAsync()).ShouldBe(2);
    }

    [Fact]
    public async Task Reconcile_SecondCallWithSameKeys_ReusesEntriesWithStableIds()
    {
        await using var db = CreateDb();
        var service = CreateService(db);

        var first = await CollectAsync(service.ReconcileAsync(SourceId, Stream(
            Claim("Shared Game"),
            Claim("Old Only"))));
        var second = await CollectAsync(service.ReconcileAsync(SourceId, Stream(
            Claim("Shared Game"),
            Claim("Old Only"))));

        second[0].Value.SourceEntryId.ShouldBe(first[0].Value.SourceEntryId);
        second[1].Value.SourceEntryId.ShouldBe(first[1].Value.SourceEntryId);
        (await db.SourceEntries.CountAsync(e => e.CatalogSourceId == SourceId)).ShouldBe(2);
    }

    [Fact]
    public async Task Derive_EntryAlreadyLinked_PreservesCurationAndCreatesNoOrphanTitle()
    {
        await using var db = CreateDb();
        db.Titles.Add(new TitleEntity
        {
            Id = 201,
            PlatformId = PlatformId,
            Name = "Curated Title",
            NormalizedName = "curatedtitle",
            EnrichmentStatus = "None",
            CreatedAt = DateTimeOffset.UtcNow
        });
        db.SourceEntries.Add(new SourceEntryEntity
        {
            Id = 101,
            CatalogSourceId = SourceId,
            EntryKey = "Game Y",
            Name = "Game Y",
            PlatformId = PlatformId,
            CreatedAt = DateTimeOffset.UtcNow
        });
        db.TitleSourceLinks.Add(new TitleSourceLinkEntity { SourceEntryId = 101, TitleId = 201 });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var service = CreateService(db);

        // The claim's name would derive a different title ("Game Y"), but the entry is
        // already curated: linked entries skip match-or-create entirely — deliberately, so
        // derivation neither rewrites the link nor manufactures an orphan title.
        var assignments = await CollectAsync(service.UpsertAsync(SourceId, Stream(Claim("Game Y"))));

        var assignment = assignments.ShouldHaveSingleItem().Value;
        assignment.TitleId.ShouldBe(201);
        assignment.TitleWasCreated.ShouldBeFalse();
        var link = (await db.TitleSourceLinks.AsNoTracking().ToListAsync()).ShouldHaveSingleItem();
        link.SourceEntryId.ShouldBe(101);
        link.TitleId.ShouldBe(201);
        (await db.Titles.CountAsync()).ShouldBe(1);
    }

    [Fact]
    public async Task Derive_ConflictingDuplicateKeyClaims_CanonicalizesOnFirstClaimWithoutOrphanTitle()
    {
        await using var db = CreateDb();
        var service = CreateService(db);

        var assignments = await CollectAsync(service.UpsertAsync(SourceId, Stream(
            new TitleClaim("games/shared.rom", "Alpha", PlatformId, IsBios: false),
            new TitleClaim("games/shared.rom", "Beta", PlatformId, IsBios: false))));

        assignments.Count.ShouldBe(2);
        assignments.ShouldAllBe(a => !a.IsError);
        var first = assignments[0].Value;
        var second = assignments[1].Value;

        // The first claim per key is canonical: the conflicting duplicate projects its
        // result instead of deriving a competing "Beta" title.
        second.SourceEntryId.ShouldBe(first.SourceEntryId);
        first.TitleId.ShouldNotBeNull();
        second.TitleId.ShouldBe(first.TitleId);
        first.TitleWasCreated.ShouldBeTrue();
        second.TitleWasCreated.ShouldBeFalse();

        var title = (await db.Titles.AsNoTracking().ToListAsync()).ShouldHaveSingleItem();
        title.Id.ShouldBe(first.TitleId!.Value);
        title.Name.ShouldBe("Alpha");
        (await SingleEntryAsync(db, "games/shared.rom")).Name.ShouldBe("Alpha");
    }

    [Fact]
    public async Task Derive_DifferentEntryKeysWithSameNormalizedName_ShareOneTitleInClaimOrder()
    {
        await using var db = CreateDb();
        var service = CreateService(db);

        var assignments = await CollectAsync(service.UpsertAsync(SourceId, Stream(
            new TitleClaim("games/alpha-us.rom", "Alpha (USA)", PlatformId, IsBios: false),
            new TitleClaim("games/beta.rom", "Beta", PlatformId, IsBios: false),
            new TitleClaim("games/alpha-eu.rom", "Alpha (Europe)", PlatformId, IsBios: false))));

        assignments.ShouldAllBe(assignment => !assignment.IsError);
        var values = assignments.Select(assignment => assignment.Value).ToList();
        values.Select(value => value.EntryKey).ShouldBe(
            ["games/alpha-us.rom", "games/beta.rom", "games/alpha-eu.rom"]);
        values.Select(value => value.TitleWasCreated).ShouldBe([true, true, false]);
        values.ShouldAllBe(value => value.TitleId > 0);
        values[0].TitleId.ShouldBe(values[2].TitleId);
        values[1].TitleId.ShouldNotBe(values[0].TitleId);
        (await db.Titles.CountAsync()).ShouldBe(2);
    }

    [Fact]
    public async Task Derive_SameNormalizedNameAcrossBatchBoundary_ReusesPersistedTitleAndReportsCreatedOnce()
    {
        await using var db = CreateDb();
        var service = CreateService(db);

        var claims = new List<TitleClaim>
        {
            new("games/alpha-us.rom", "Alpha (USA)", PlatformId, IsBios: false)
        };
        claims.AddRange(FillerClaims(499));
        claims.Add(new TitleClaim("games/alpha-eu.rom", "Alpha (Europe)", PlatformId, IsBios: false));

        var assignments = await CollectAsync(service.UpsertAsync(SourceId, Stream(claims)));

        assignments.Count.ShouldBe(501);
        assignments.ShouldAllBe(assignment => !assignment.IsError && assignment.Value.TitleId > 0);
        assignments.Select(assignment => assignment.Value.EntryKey)
            .ShouldBe(claims.Select(claim => claim.EntryKey).ToList());
        assignments[0].Value.TitleWasCreated.ShouldBeTrue();
        assignments[500].Value.TitleWasCreated.ShouldBeFalse();
        assignments[500].Value.TitleId.ShouldBe(assignments[0].Value.TitleId);
        (await db.Titles.CountAsync(title => title.NormalizedName == "alpha")).ShouldBe(1);
    }

    [Fact]
    public async Task Derive_DuplicateKeyAcrossBatchBoundary_ProjectsSettledResultWithoutReapplyingFacts()
    {
        await using var db = CreateDb();
        var service = CreateService(db);

        // Claim 0 establishes key "X" in the first batch; 500 fillers push the conflicting
        // duplicate (new name, reclassified as BIOS) into the SECOND batch of the same call.
        var claims = new List<TitleClaim> { new("X", "Alpha", PlatformId, IsBios: false) };
        claims.AddRange(FillerClaims(500));
        claims.Add(new TitleClaim("X", "Beta", PlatformId, IsBios: true));

        var assignments = await CollectAsync(service.UpsertAsync(SourceId, Stream(claims)));

        assignments.Count.ShouldBe(502);
        assignments.ShouldAllBe(a => !a.IsError);
        var values = assignments.Select(a => a.Value).ToList();
        values.Select(v => v.EntryKey).ShouldBe(claims.Select(c => c.EntryKey).ToList());

        var first = values[0];
        var last = values[501];
        first.TitleWasCreated.ShouldBeTrue();
        first.TitleId.ShouldNotBeNull();

        // Canonicalization is stream-level: the settled key's facts are not reapplied. The
        // discriminating fact is that the duplicate's BIOS flag does not delete the link
        // derived by the first batch — and its name does not stick either.
        var link = (await db.TitleSourceLinks.AsNoTracking()
                .Where(l => l.SourceEntryId == first.SourceEntryId)
                .ToListAsync())
            .ShouldHaveSingleItem();
        link.TitleId.ShouldBe(first.TitleId!.Value);
        (await SingleEntryAsync(db, "X")).Name.ShouldBe("Alpha");

        last.SourceEntryId.ShouldBe(first.SourceEntryId);
        last.TitleId.ShouldBe(first.TitleId);
        last.TitleWasCreated.ShouldBeFalse();
        (await db.Titles.CountAsync(t => t.Name == "Alpha")).ShouldBe(1);
    }

    [Fact]
    public async Task Reconcile_ClaimConflictsWithEstablishedPlatform_RejectsEveryClaimButStampsEntrySeen()
    {
        await using var db = CreateDb();
        db.Titles.Add(new TitleEntity
        {
            Id = 201,
            PlatformId = PlatformId,
            Name = "Curated Title",
            NormalizedName = "curatedtitle",
            EnrichmentStatus = "None",
            CreatedAt = DateTimeOffset.UtcNow
        });
        db.SourceEntries.Add(new SourceEntryEntity
        {
            Id = 101,
            CatalogSourceId = SourceId,
            EntryKey = "K",
            Name = "Established Name",
            PlatformId = PlatformId,
            LastReconcileRunId = "oldrun",
            CreatedAt = DateTimeOffset.UtcNow
        });
        db.TitleSourceLinks.Add(new TitleSourceLinkEntity { SourceEntryId = 101, TitleId = 201 });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var service = CreateService(db);

        var conflicted = new TitleClaim("K", "New Name", OtherPlatformId, IsBios: false);
        var assignments = await CollectAsync(service.ReconcileAsync(SourceId, Stream(
            conflicted,
            conflicted)));

        // Every claim position for the conflicted key — including the in-stream duplicate —
        // is answered with the same validation error.
        assignments.Count.ShouldBe(2);
        foreach (var assignment in assignments)
        {
            assignment.IsError.ShouldBeTrue();
            assignment.FirstError.Type.ShouldBe(ErrorType.Validation);
            assignment.FirstError.Code.ShouldBe("Derivation.PlatformConflict");
        }

        // No facts applied: platform, name, and link are untouched, and no cross-platform
        // title was manufactured...
        var entry = await SingleEntryAsync(db, "K");
        entry.Id.ShouldBe(101);
        entry.PlatformId.ShouldBe(PlatformId);
        entry.Name.ShouldBe("Established Name");
        var link = (await db.TitleSourceLinks.AsNoTracking().ToListAsync()).ShouldHaveSingleItem();
        link.SourceEntryId.ShouldBe(101);
        link.TitleId.ShouldBe(201);
        (await db.Titles.CountAsync()).ShouldBe(1);

        // ...but the entry WAS stamped seen — which is exactly why this ReconcileAsync call,
        // whose only claim was the conflicted one, did not delete it.
        entry.LastReconcileRunId.ShouldNotBeNull();
        entry.LastReconcileRunId.ShouldNotBe("oldrun");
    }

    [Fact]
    public async Task Derive_ConflictedKeyDuplicatedAcrossBatchBoundary_ProjectsErrorAtEveryPosition()
    {
        await using var db = CreateDb();
        db.Titles.Add(new TitleEntity
        {
            Id = 201,
            PlatformId = PlatformId,
            Name = "Curated Title",
            NormalizedName = "curatedtitle",
            EnrichmentStatus = "None",
            CreatedAt = DateTimeOffset.UtcNow
        });
        db.SourceEntries.Add(new SourceEntryEntity
        {
            Id = 101,
            CatalogSourceId = SourceId,
            EntryKey = "K",
            Name = "Established Name",
            PlatformId = PlatformId,
            LastReconcileRunId = "oldrun",
            CreatedAt = DateTimeOffset.UtcNow
        });
        db.TitleSourceLinks.Add(new TitleSourceLinkEntity { SourceEntryId = 101, TitleId = 201 });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var service = CreateService(db);

        // Claim 0 conflicts with the established platform; 499 fillers pad the first batch
        // to exactly 500 so the duplicate lands in the SECOND batch. The duplicate carries
        // MATCHING platform facts on purpose: the run's conflict disposition must win even
        // when the duplicate's own facts would be acceptable — a stamped-but-conflicted
        // entry is not settled.
        var claims = new List<TitleClaim> { new("K", "New Name", OtherPlatformId, IsBios: false) };
        claims.AddRange(FillerClaims(499));
        claims.Add(new TitleClaim("K", "Established Name", PlatformId, IsBios: false));

        var assignments = await CollectAsync(service.UpsertAsync(SourceId, Stream(claims)));

        assignments.Count.ShouldBe(501);
        foreach (int conflictedPosition in new[] { 0, 500 })
        {
            assignments[conflictedPosition].IsError.ShouldBeTrue(
                $"assignment {conflictedPosition} should project the conflict error");
            assignments[conflictedPosition].FirstError.Type.ShouldBe(ErrorType.Validation);
            assignments[conflictedPosition].FirstError.Code.ShouldBe("Derivation.PlatformConflict");
        }

        // Fillers are unaffected and arrive in claim order between the conflicted positions.
        for (int i = 1; i < 500; i++)
        {
            assignments[i].IsError.ShouldBeFalse();
            assignments[i].Value.EntryKey.ShouldBe(claims[i].EntryKey);
        }

        // No facts applied at either position: platform, name, and link are untouched, and
        // no title was manufactured for 'K' — only the curated title and one per filler.
        var entry = await SingleEntryAsync(db, "K");
        entry.Id.ShouldBe(101);
        entry.PlatformId.ShouldBe(PlatformId);
        entry.Name.ShouldBe("Established Name");
        var link = (await db.TitleSourceLinks.AsNoTracking()
                .Where(l => l.SourceEntryId == 101)
                .ToListAsync())
            .ShouldHaveSingleItem();
        link.TitleId.ShouldBe(201);
        (await db.Titles.CountAsync(t => t.PlatformId == OtherPlatformId)).ShouldBe(0);
        (await db.Titles.CountAsync()).ShouldBe(500);
    }

    [Fact]
    public async Task Derive_UnlinkedEntryWithEstablishedPlatform_ConflictingClaimDerivesNothing()
    {
        await using var db = CreateDb();
        db.SourceEntries.Add(new SourceEntryEntity
        {
            Id = 101,
            CatalogSourceId = SourceId,
            EntryKey = "K",
            Name = "K",
            PlatformId = PlatformId,
            CreatedAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var service = CreateService(db);

        var assignments = await CollectAsync(service.UpsertAsync(SourceId, Stream(
            new TitleClaim("K", "K", OtherPlatformId, IsBios: false))));

        // Conflict rejection short-circuits derivation even where an unlinked entry would
        // otherwise match-or-create: no title appears on the claimed platform.
        var assignment = assignments.ShouldHaveSingleItem();
        assignment.IsError.ShouldBeTrue();
        assignment.FirstError.Code.ShouldBe("Derivation.PlatformConflict");
        (await db.Titles.CountAsync()).ShouldBe(0);
        (await db.TitleSourceLinks.CountAsync()).ShouldBe(0);
        (await SingleEntryAsync(db, "K")).PlatformId.ShouldBe(PlatformId);
    }

    [Fact]
    public async Task Derive_LinkedEntryReclassifiedAsBios_DeletesLinkAndReportsNoTitle()
    {
        await using var db = CreateDb();
        db.Titles.Add(new TitleEntity
        {
            Id = 201,
            PlatformId = PlatformId,
            Name = "Curated Title",
            NormalizedName = "curatedtitle",
            EnrichmentStatus = "None",
            CreatedAt = DateTimeOffset.UtcNow
        });
        db.SourceEntries.Add(new SourceEntryEntity
        {
            Id = 101,
            CatalogSourceId = SourceId,
            EntryKey = "Game Y",
            Name = "Game Y",
            PlatformId = PlatformId,
            CreatedAt = DateTimeOffset.UtcNow
        });
        db.TitleSourceLinks.Add(new TitleSourceLinkEntity { SourceEntryId = 101, TitleId = 201 });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var service = CreateService(db);

        // BIOS classification outranks curation: the link is removed, the entry keeps its
        // identity, and the now-orphaned title's cleanup is a separate concern.
        var assignments = await CollectAsync(service.UpsertAsync(SourceId, Stream(
            new TitleClaim("Game Y", "Game Y", PlatformId, IsBios: true))));

        var assignment = assignments.ShouldHaveSingleItem().Value;
        assignment.SourceEntryId.ShouldBe(101);
        assignment.TitleId.ShouldBeNull();
        assignment.TitleWasCreated.ShouldBeFalse();
        (await db.TitleSourceLinks.CountAsync()).ShouldBe(0);
        (await SingleEntryAsync(db, "Game Y")).Id.ShouldBe(101);
        (await db.Titles.CountAsync(t => t.Id == 201)).ShouldBe(1);
    }

    [Fact]
    public async Task Derive_ExistingEntryNameDiffersFromClaim_RefreshesNameAndLeavesMatchingEntryUntouched()
    {
        await using var db = CreateDb();
        db.SourceEntries.AddRange(
            new SourceEntryEntity
            {
                Id = 101,
                CatalogSourceId = SourceId,
                EntryKey = "games/alpha.rom",
                Name = "Old Name",
                PlatformId = PlatformId,
                CreatedAt = DateTimeOffset.UtcNow
            },
            new SourceEntryEntity
            {
                Id = 102,
                CatalogSourceId = SourceId,
                EntryKey = "games/beta.rom",
                Name = "Beta Name",
                PlatformId = PlatformId,
                CreatedAt = DateTimeOffset.UtcNow
            });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var service = CreateService(db);

        await CollectAsync(service.UpsertAsync(SourceId, Stream(
            new TitleClaim("games/alpha.rom", "Corrected Name", PlatformId, IsBios: false),
            new TitleClaim("games/beta.rom", "Beta Name", PlatformId, IsBios: false))));

        // Keys and names differ for non-DAT providers: the claim's name wins for the stale
        // entry, while the already-matching entry is left as-is.
        (await SingleEntryAsync(db, "games/alpha.rom")).Name.ShouldBe("Corrected Name");
        (await SingleEntryAsync(db, "games/beta.rom")).Name.ShouldBe("Beta Name");
    }

    [Fact]
    public async Task Derive_BiosAndUnroutedClaims_GetEntryIdentityButNeverLinks()
    {
        await using var db = CreateDb();
        var service = CreateService(db);

        var assignments = await CollectAsync(service.UpsertAsync(SourceId, Stream(
            new TitleClaim("[BIOS] Firmware", "[BIOS] Firmware", PlatformId, IsBios: true),
            new TitleClaim("Unrouted Game", "Unrouted Game", PlatformId: null, IsBios: false))));

        assignments.Count.ShouldBe(2);
        assignments.ShouldAllBe(a => !a.IsError && a.Value.TitleId == null && !a.Value.TitleWasCreated);
        var entryKeys = await db.SourceEntries.AsNoTracking()
            .Select(e => e.EntryKey)
            .ToListAsync();
        entryKeys.OrderBy(k => k).ShouldBe(["[BIOS] Firmware", "Unrouted Game"]);
        (await db.TitleSourceLinks.CountAsync()).ShouldBe(0);
        (await db.Titles.CountAsync()).ShouldBe(0);
    }

    [Fact]
    public async Task Derive_NullPlatformEntry_StampedByPlatformfulClaimButEstablishedPlatformNeverMoves()
    {
        await using var db = CreateDb();
        var service = CreateService(db);

        await CollectAsync(service.UpsertAsync(SourceId, Stream(
            new TitleClaim("Late Routed", "Late Routed", PlatformId: null, IsBios: false))));
        (await SingleEntryAsync(db, "Late Routed")).PlatformId.ShouldBeNull();

        // A later platformful claim stamps the null platform...
        await CollectAsync(service.UpsertAsync(SourceId, Stream(Claim("Late Routed"))));
        (await SingleEntryAsync(db, "Late Routed")).PlatformId.ShouldBe(PlatformId);

        // ...but an established platform never moves.
        await CollectAsync(service.UpsertAsync(SourceId, Stream(
            new TitleClaim("Late Routed", "Late Routed", OtherPlatformId, IsBios: false))));
        (await SingleEntryAsync(db, "Late Routed")).PlatformId.ShouldBe(PlatformId);
    }

    [Fact]
    public async Task Reconcile_CompletedStream_DeletesAllThisSourcesUnseenEntriesCascadingLinks()
    {
        await using var db = CreateDb();
        await SeedStaleEntriesAsync(db, gameStillReferencesEntry102: false);
        var service = CreateService(db);

        await CollectAsync(service.ReconcileAsync(SourceId, Stream(Claim("Fresh Game"))));

        var survivingKeys = await db.SourceEntries.AsNoTracking()
            .Select(e => e.EntryKey)
            .ToListAsync();
        // Deletion is purely stamp-based: every unseen entry of this source goes, linked or
        // not; the other source's entry survives and the link cascades at the database.
        survivingKeys.OrderBy(k => k).ShouldBe(["Fresh Game", "Other Source Game"]);
        (await db.TitleSourceLinks.CountAsync(l => l.SourceEntryId == 101)).ShouldBe(0);
    }

    [Fact]
    public async Task Reconcile_UnseenEntryStillReferencedByProviderPayload_ThrowsFromRestrictConstraint()
    {
        await using var db = CreateDb();
        await SeedStaleEntriesAsync(db, gameStillReferencesEntry102: true);
        var service = CreateService(db);

        // Documented contract: reconcile deletion knows nothing about provider payloads.
        // A provider whose payload rows (here a DatGame, Restrict FK) still reference an
        // unseen entry must retire those payloads first — or use UpsertAsync and prune
        // provider-side, as DAT does. ExecuteDelete surfaces the constraint unwrapped.
        var exception = await Should.ThrowAsync<PostgresException>(async () =>
            await CollectAsync(service.ReconcileAsync(SourceId, Stream(Claim("Fresh Game")))));
        exception.SqlState.ShouldBe(PostgresErrorCodes.RestrictViolation);
        exception.Message.ShouldContain("foreign key constraint");
    }

    [Fact]
    public async Task Upsert_CompletedStream_DeletesNothing()
    {
        await using var db = CreateDb();
        await SeedStaleEntriesAsync(db, gameStillReferencesEntry102: true);
        var service = CreateService(db);

        await CollectAsync(service.UpsertAsync(SourceId, Stream(Claim("Fresh Game"))));

        var survivingKeys = await db.SourceEntries.AsNoTracking()
            .Select(e => e.EntryKey)
            .ToListAsync();
        survivingKeys.OrderBy(k => k).ShouldBe(
            ["Fresh Game", "Other Source Game", "Stale Linked", "Stale Unlinked"]);
        (await db.TitleSourceLinks.CountAsync(l => l.SourceEntryId == 101)).ShouldBe(1);
    }

    [Fact]
    public async Task Reconcile_StampsEverySeenEntryWithOneRunIdPerCall()
    {
        await using var db = CreateDb();
        var service = CreateService(db);

        await CollectAsync(service.ReconcileAsync(SourceId, Stream(Claim("Game A"), Claim("Game B"))));
        var firstStamps = await db.SourceEntries.AsNoTracking()
            .Select(e => e.LastReconcileRunId)
            .ToListAsync();
        firstStamps.Count.ShouldBe(2);
        firstStamps.ShouldAllBe(stamp => !string.IsNullOrEmpty(stamp));
        firstStamps.Distinct().Count().ShouldBe(1);

        await CollectAsync(service.ReconcileAsync(SourceId, Stream(Claim("Game A"), Claim("Game B"))));
        var secondStamps = await db.SourceEntries.AsNoTracking()
            .Select(e => e.LastReconcileRunId)
            .ToListAsync();
        secondStamps.Distinct().Count().ShouldBe(1);
        secondStamps[0].ShouldNotBe(firstStamps[0]);
    }

    [Fact]
    public async Task Derive_EmptyEntryKeyClaim_YieldsValidationErrorInOrderWithoutDisturbingOthers()
    {
        await using var db = CreateDb();
        var service = CreateService(db);

        var assignments = await CollectAsync(service.ReconcileAsync(SourceId, Stream(
            Claim("Valid A"),
            new TitleClaim("", "Broken", PlatformId, IsBios: false),
            Claim("Valid B"))));

        assignments.Count.ShouldBe(3);
        assignments[0].IsError.ShouldBeFalse();
        assignments[0].Value.EntryKey.ShouldBe("Valid A");
        assignments[1].IsError.ShouldBeTrue();
        assignments[1].FirstError.Type.ShouldBe(ErrorType.Validation);
        assignments[1].FirstError.Code.ShouldBe("Derivation.EmptyEntryKey");
        assignments[2].IsError.ShouldBeFalse();
        assignments[2].Value.EntryKey.ShouldBe("Valid B");
        (await db.SourceEntries.CountAsync()).ShouldBe(2);
    }

    [Fact]
    public async Task Derive_AfterEnumerationCompletes_LeavesChangeTrackerEmpty()
    {
        await using var db = CreateDb();
        var service = CreateService(db);

        await CollectAsync(service.ReconcileAsync(SourceId, Stream(
            Claim("Game A"),
            Claim("Game B"))));

        // Flush protocol: everything is saved and the tracker is cleared at batch boundaries.
        db.ChangeTracker.Entries().ShouldBeEmpty();
    }

    public void Dispose() => _connection.Dispose();

    private RomdDbContext CreateDb() => new(_contextOptions);

    private static TitleDerivationService CreateService(RomdDbContext db) =>
        new(db, new TitleMatcher(new TitleRepository(db)));

    private static TitleClaim Claim(string name) => new(name, name, PlatformId, IsBios: false);

    /// <summary>Distinct-key, same-platform padding claims used to force a batch boundary.</summary>
    private static IEnumerable<TitleClaim> FillerClaims(int count) =>
        Enumerable.Range(1, count)
            .Select(i => new TitleClaim($"Filler {i:D3}", $"Filler {i:D3}", PlatformId, IsBios: false));

    private static IAsyncEnumerable<TitleClaim> Stream(params TitleClaim[] claims) =>
        Stream((IEnumerable<TitleClaim>)claims);

    private static async IAsyncEnumerable<TitleClaim> Stream(IEnumerable<TitleClaim> claims)
    {
        await Task.Yield();
        foreach (var claim in claims)
        {
            yield return claim;
        }
    }

    private static async Task<List<ErrorOr<ClaimAssignment>>> CollectAsync(
        IAsyncEnumerable<ErrorOr<ClaimAssignment>> assignments)
    {
        var collected = new List<ErrorOr<ClaimAssignment>>();
        await foreach (var assignment in assignments)
        {
            collected.Add(assignment);
        }

        return collected;
    }

    private static Task<SourceEntryEntity> SingleEntryAsync(RomdDbContext db, string entryKey) =>
        db.SourceEntries.AsNoTracking().SingleAsync(e => e.EntryKey == entryKey);

    /// <summary>
    ///     Stale graph for the deletion tests: entry 101 ("Stale Linked", linked to a title),
    ///     entry 102 ("Stale Unlinked", optionally still referenced by a DatGame payload row),
    ///     and entry 103 in a different catalog source. None carry a current run stamp.
    /// </summary>
    private static async Task SeedStaleEntriesAsync(RomdDbContext db, bool gameStillReferencesEntry102)
    {
        db.Titles.Add(new TitleEntity
        {
            Id = 201,
            PlatformId = PlatformId,
            Name = "Stale Title",
            NormalizedName = "staletitle",
            EnrichmentStatus = "None",
            CreatedAt = DateTimeOffset.UtcNow
        });
        db.SourceEntries.AddRange(
            new SourceEntryEntity
            {
                Id = 101,
                CatalogSourceId = SourceId,
                EntryKey = "Stale Linked",
                Name = "Stale Linked",
                PlatformId = PlatformId,
                CreatedAt = DateTimeOffset.UtcNow
            },
            new SourceEntryEntity
            {
                Id = 102,
                CatalogSourceId = SourceId,
                EntryKey = "Stale Unlinked",
                Name = "Stale Unlinked",
                PlatformId = PlatformId,
                CreatedAt = DateTimeOffset.UtcNow
            },
            new SourceEntryEntity
            {
                Id = 103,
                CatalogSourceId = OtherSourceId,
                EntryKey = "Other Source Game",
                Name = "Other Source Game",
                CreatedAt = DateTimeOffset.UtcNow
            });
        db.TitleSourceLinks.Add(new TitleSourceLinkEntity { SourceEntryId = 101, TitleId = 201 });
        if (gameStillReferencesEntry102)
        {
            db.DatGames.Add(new DatGameEntity
            {
                Id = 1000,
                DatFileId = DatFileId,
                SourceEntryId = 102,
                Name = "Stale Unlinked",
                CreatedAt = DateTimeOffset.UtcNow
            });
        }

        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
    }

    private static void SeedCatalog(RomdDbContext db)
    {
        db.Platforms.AddRange(
            new PlatformEntity
            {
                Id = PlatformId,
                Name = "Super Nintendo",
                Ownership = Romd.Domain.ReferenceData.ReferenceOwnership.Romd, BuiltInVersion = 1, BaseName = "Super Nintendo", BaseCompactLabel = "Super Nintendo", CanonicalKey = "snes", ShortName = "snes",
                CreatedAt = DateTimeOffset.UtcNow
            },
            new PlatformEntity
            {
                Id = OtherPlatformId,
                Name = "Nintendo 64",
                Ownership = Romd.Domain.ReferenceData.ReferenceOwnership.Romd, BuiltInVersion = 1, BaseName = "Nintendo 64", BaseCompactLabel = "Nintendo 64", CanonicalKey = "n64", ShortName = "n64",
                CreatedAt = DateTimeOffset.UtcNow
            });
        db.Files.Add(new FileEntityPersistence
        {
            Id = 5,
            Sha256 = NewSha256(5),
            Size = 1,
            SizeOnDisk = 1,
            CreatedAt = DateTimeOffset.UtcNow
        });
        db.DatFiles.Add(new DatFileEntity
        {
            Source = new DatSourceEntity
            {
                Id = SourceId,
                CatalogSource = new CatalogSourceEntity { Id = SourceId, Kind = "Dat", Status = "Active" }
            },
            Id = DatFileId,
            Name = "Derivation DAT",
            Description = "Derivation DAT",
            Type = "NoIntro",
            PlatformId = PlatformId,
            OriginalFilename = "derivation.dat",
            FileId = 5,
            CreatedAt = DateTimeOffset.UtcNow
        });
        db.CatalogSources.Add(new CatalogSourceEntity { Id = OtherSourceId, Kind = "Dat", Status = "Active" });
        db.SaveChanges();
        db.ChangeTracker.Clear();
    }

    private static Sha256 NewSha256(byte firstByte)
    {
        var bytes = new byte[Sha256.ByteLength];
        bytes[0] = firstByte;
        return Sha256.FromBytes(bytes);
    }
}
