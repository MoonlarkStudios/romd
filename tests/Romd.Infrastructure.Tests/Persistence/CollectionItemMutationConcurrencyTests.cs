using System.Data.Common;
using ErrorOr;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Romd.Admin.Application.Collections;
using Romd.Admin.Application.Collections.Commands.AddTitle;
using Romd.Admin.Application.Collections.Commands.RemoveTitle;
using Romd.Admin.Application.Collections.Commands.ReorderItems;
using Romd.Admin.Application.Collections.Commands.UpdateCollection;
using Romd.Admin.Application.Collections.Commands.UpdateItemNote;
using Romd.Application.Common.Ids;
using Romd.Consumer.Application.Browse;
using Romd.Contracts.Management.Collections;
using Romd.Domain.Collections;
using Romd.Persistence;
using Romd.Persistence.Entities;
using Romd.Persistence.ReferenceData;
using Romd.Persistence.Repositories;
using Romd.PostgreSql.TestSupport;
using Shouldly;
using Xunit;

using CollectionItem = Romd.Domain.Collections.CollectionItem;

namespace Romd.Infrastructure.Tests.Persistence;

/// <summary>
///     Collection item commands serialize on a collection row lock taken before membership is
///     read. Each interleaving test pauses one request after it has acquired the lock, starts the
///     overlapping request on an independent context, and releases the first request only once
///     PostgreSQL reports the second waiting on a lock.
/// </summary>
public sealed class CollectionItemMutationConcurrencyTests : IDisposable
{
    private const int CollectionId = 30;
    private static readonly DateTimeOffset AddedAt = new(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);
    private readonly PostgreSqlTestDatabase _database = PostgreSqlTestDatabase.Create();

    [Fact]
    public async Task AddTitle_ConcurrentAddFromStaleSnapshot_PreservesBothAdditions()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await SeedAsync();

        var (first, second) = await InterleaveAsync(
            (context, repository, ct) => AddHandler(context, repository)
                .HandleAsync(new AddTitleToCollectionCommand(CollectionId, 23, "first"), ct),
            (context, repository, ct) => AddHandler(context, repository)
                .HandleAsync(new AddTitleToCollectionCommand(CollectionId, 22, "second"), ct),
            timeout.Token);

        first.IsError.ShouldBeFalse();
        second.IsError.ShouldBeFalse();
        first.Value.TitleId.ShouldBe(IdCoder.Encode(23));
        first.Value.Note.ShouldBe("first");
        first.Value.SortOrder.ShouldBe(2);
        second.Value.TitleId.ShouldBe(IdCoder.Encode(22));
        second.Value.Note.ShouldBe("second");
        second.Value.SortOrder.ShouldBe(3);
        var collection = await LoadAsync(timeout.Token);
        AssertCollectionPropertiesPreserved(collection, "Original");
        var items = Ordered(collection);
        items.Count.ShouldBe(4);
        items.Select(i => i.TitleId).ShouldBe([21, 20, 23, 22]);
        items.Select(i => i.SortOrder).ShouldBe([0, 1, 2, 3]);
        items.Select(i => i.Note).ShouldBe(["Play first", "Play next", "first", "second"]);
        AssertSeededItemsPreserved(items);
    }

    [Fact]
    public async Task RemoveTitle_ConcurrentAdd_PreservesAdditionAndCompactsOrder()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await SeedAsync();

        var (removal, addition) = await InterleaveAsync(
            (context, repository, ct) => RemoveHandler(context, repository)
                .HandleAsync(new RemoveTitleFromCollectionCommand(CollectionId, 21), ct),
            (context, repository, ct) => AddHandler(context, repository)
                .HandleAsync(new AddTitleToCollectionCommand(CollectionId, 22, "added"), ct),
            timeout.Token);

        removal.IsError.ShouldBeFalse();
        addition.IsError.ShouldBeFalse();
        var items = Ordered(await LoadAsync(timeout.Token));
        items.Select(i => i.TitleId).ShouldBe([20, 22]);
        items.Select(i => i.SortOrder).ShouldBe([0, 1]);
        items[0].Id.ShouldBe(41);
        items[0].Note.ShouldBe("Play next");
        items[0].AddedAt.ShouldBe(AddedAt.AddDays(1));
    }

    [Fact]
    public async Task AddTitle_ConcurrentRemove_RemovalCompactsOrderIncludingAddition()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await SeedAsync();

        var (addition, removal) = await InterleaveAsync(
            (context, repository, ct) => AddHandler(context, repository)
                .HandleAsync(new AddTitleToCollectionCommand(CollectionId, 22, null), ct),
            (context, repository, ct) => RemoveHandler(context, repository)
                .HandleAsync(new RemoveTitleFromCollectionCommand(CollectionId, 21), ct),
            timeout.Token);

        addition.IsError.ShouldBeFalse();
        removal.IsError.ShouldBeFalse();
        removal.Value.Items.Items.Select(i => i.SortOrder).ShouldBe([0, 1]);
        var items = Ordered(await LoadAsync(timeout.Token));
        items.Select(i => i.TitleId).ShouldBe([20, 22]);
        items.Select(i => i.SortOrder).ShouldBe([0, 1]);
    }

    [Fact]
    public async Task UpdateItemNote_ConcurrentReorder_PreservesNoteAndOrder()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await SeedAsync();

        var (reorder, note) = await InterleaveAsync(
            (context, repository, ct) => ReorderHandler(context, repository)
                .HandleAsync(new ReorderCollectionItemsCommand(CollectionId, [20, 21]), ct),
            (context, repository, ct) => NoteHandler(context, repository)
                .HandleAsync(new UpdateCollectionItemNoteCommand(CollectionId, 21, "Updated"), ct),
            timeout.Token);

        reorder.IsError.ShouldBeFalse();
        note.IsError.ShouldBeFalse();
        var items = Ordered(await LoadAsync(timeout.Token));
        items.Select(i => i.Id).ShouldBe([41, 40]);
        items.Select(i => i.SortOrder).ShouldBe([0, 1]);
        items.Select(i => i.Note).ShouldBe(["Play next", "Updated"]);
        items.Select(i => i.AddedAt).ShouldBe([AddedAt.AddDays(1), AddedAt]);
    }

    [Fact]
    public async Task AddTitle_ConcurrentDetailsUpdate_PreservesDetailsAndMembership()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await SeedAsync();

        var (addition, details) = await InterleaveAsync(
            (context, repository, ct) => AddHandler(context, repository)
                .HandleAsync(new AddTitleToCollectionCommand(CollectionId, 22, null), ct),
            (context, repository, ct) => DetailsHandler(context, repository)
                .HandleAsync(new UpdateCollectionCommand(CollectionId, "Renamed", "New description", null, 10), ct),
            timeout.Token);

        addition.IsError.ShouldBeFalse();
        details.IsError.ShouldBeFalse();
        var collection = await LoadAsync(timeout.Token);
        AssertCollectionPropertiesPreserved(collection, "Renamed");
        collection.Description.ShouldBe("New description");
        collection.PlatformId.ShouldBe(10);
        var items = Ordered(collection);
        items.Select(i => i.TitleId).ShouldBe([21, 20, 22]);
        AssertSeededItemsPreserved(items);
    }

    [Fact]
    public async Task ReorderItems_MembershipChangedWhileWaiting_ReturnsReorderMismatchAndWritesNothing()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await SeedAsync();

        var (addition, reorder) = await InterleaveAsync(
            (context, repository, ct) => AddHandler(context, repository)
                .HandleAsync(new AddTitleToCollectionCommand(CollectionId, 22, null), ct),
            (context, repository, ct) => ReorderHandler(context, repository)
                .HandleAsync(new ReorderCollectionItemsCommand(CollectionId, [20, 21]), ct),
            timeout.Token);

        addition.IsError.ShouldBeFalse();
        reorder.FirstError.Code.ShouldBe("Collections.ReorderMismatch");
        var items = Ordered(await LoadAsync(timeout.Token));
        items.Select(i => i.TitleId).ShouldBe([21, 20, 22]);
        items.Select(i => i.SortOrder).ShouldBe([0, 1, 2]);
    }

    [Fact]
    public async Task AddTitle_ConcurrentDuplicateAdd_SecondReturnsDuplicateTitle()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await SeedAsync();

        var (first, second) = await InterleaveAsync(
            (context, repository, ct) => AddHandler(context, repository)
                .HandleAsync(new AddTitleToCollectionCommand(CollectionId, 22, "first"), ct),
            (context, repository, ct) => AddHandler(context, repository)
                .HandleAsync(new AddTitleToCollectionCommand(CollectionId, 22, "second"), ct),
            timeout.Token);

        first.IsError.ShouldBeFalse();
        second.FirstError.Code.ShouldBe("Collections.DuplicateTitle");
        second.FirstError.Type.ShouldBe(ErrorType.Conflict);
        var items = Ordered(await LoadAsync(timeout.Token));
        items.Select(i => i.TitleId).ShouldBe([21, 20, 22]);
        items[2].Note.ShouldBe("first");
    }

    [Fact]
    public async Task RemoveTitle_ConcurrentRemoveOfSameTitle_SecondReturnsTitleNotInCollection()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await SeedAsync();

        var (first, second) = await InterleaveAsync(
            (context, repository, ct) => RemoveHandler(context, repository)
                .HandleAsync(new RemoveTitleFromCollectionCommand(CollectionId, 21), ct),
            (context, repository, ct) => RemoveHandler(context, repository)
                .HandleAsync(new RemoveTitleFromCollectionCommand(CollectionId, 21), ct),
            timeout.Token);

        first.IsError.ShouldBeFalse();
        second.FirstError.Code.ShouldBe("Collections.TitleNotInCollection");
        var items = Ordered(await LoadAsync(timeout.Token));
        items.Select(i => i.Id).ShouldBe([41]);
        items[0].SortOrder.ShouldBe(0);
    }

    [Fact]
    public async Task UpdateItemNote_ItemRemovedWhileWaiting_ReturnsTitleNotInCollection()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await SeedAsync();

        var (removal, note) = await InterleaveAsync(
            (context, repository, ct) => RemoveHandler(context, repository)
                .HandleAsync(new RemoveTitleFromCollectionCommand(CollectionId, 21), ct),
            (context, repository, ct) => NoteHandler(context, repository)
                .HandleAsync(new UpdateCollectionItemNoteCommand(CollectionId, 21, "Too late"), ct),
            timeout.Token);

        removal.IsError.ShouldBeFalse();
        note.FirstError.Code.ShouldBe("Collections.TitleNotInCollection");
        var items = Ordered(await LoadAsync(timeout.Token));
        items.Select(i => i.TitleId).ShouldBe([20]);
        items[0].Note.ShouldBe("Play next");
    }

    [Fact]
    public async Task AddTitle_CancelledAfterStaging_RollsBackAndReleasesLock()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await SeedAsync();
        await using var context = _database.CreateContext();
        using var request = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
        var gate = new Gate(GatePoint.AfterAddStaged);

        var addition = AddHandler(context, new GatedCollectionRepository(Repository(context), gate))
            .HandleAsync(new AddTitleToCollectionCommand(CollectionId, 22, null), request.Token);
        await gate.Reached.Task.WaitAsync(timeout.Token);
        await request.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(addition);
        await using var next = _database.CreateContext();
        var after = await AddHandler(next, Repository(next))
            .HandleAsync(new AddTitleToCollectionCommand(CollectionId, 23, null), timeout.Token);
        after.IsError.ShouldBeFalse();
        var items = Ordered(await LoadAsync(timeout.Token));
        items.Select(i => i.TitleId).ShouldBe([21, 20, 23]);
        AssertSeededItemsPreserved(items);
    }

    [Fact]
    public async Task AddTitle_CommitFails_LeavesNoPartialWrite()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await SeedAsync();
        await using var context = new RomdDbContext(
            _database.CreateOptions(builder => builder.AddInterceptors(new FailingCommitInterceptor())));

        await Should.ThrowAsync<InvalidOperationException>(AddHandler(context, Repository(context))
            .HandleAsync(new AddTitleToCollectionCommand(CollectionId, 22, null), timeout.Token));

        var collection = await LoadAsync(timeout.Token);
        AssertCollectionPropertiesPreserved(collection, "Original");
        var items = Ordered(collection);
        items.Select(i => i.TitleId).ShouldBe([21, 20]);
        AssertSeededItemsPreserved(items);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task AddTitle_ItemChangesAfterCommit_ReturnsAcceptedRepresentation(bool remove, bool beyondFirstPage)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await SeedAsync();
        int[] extraTitleIds = beyondFirstPage ? await SeedAdditionalItemsAsync(timeout.Token) : [];
        var gate = new Gate(GatePoint.AfterCommit);
        await using var context = new RomdDbContext(
            _database.CreateOptions(builder => builder.AddInterceptors(new CommittedGateInterceptor(gate))));

        var addition = AddHandler(context, Repository(context))
            .HandleAsync(new AddTitleToCollectionCommand(CollectionId, 22, "accepted note"), timeout.Token);
        await gate.Reached.Task.WaitAsync(timeout.Token);

        try
        {
            // The transaction committed and released its row lock, but its caller has not received the result.
            await using var competingContext = _database.CreateContext();
            if (remove)
            {
                var removal = await RemoveHandler(competingContext, Repository(competingContext))
                    .HandleAsync(new RemoveTitleFromCollectionCommand(CollectionId, 22), timeout.Token);
                removal.IsError.ShouldBeFalse();
            }
            else
            {
                var update = await NoteHandler(competingContext, Repository(competingContext))
                    .HandleAsync(new UpdateCollectionItemNoteCommand(CollectionId, 22, "later note"), timeout.Token);
                update.IsError.ShouldBeFalse();
            }

            var items = Ordered(await LoadAsync(timeout.Token));
            int[] expectedTitleIds = [21, 20, .. extraTitleIds];
            if (!remove)
                expectedTitleIds = [.. expectedTitleIds, 22];
            items.Select(item => item.TitleId).ShouldBe(expectedTitleIds);
            items.Select(item => item.SortOrder).ShouldBe(Enumerable.Range(0, expectedTitleIds.Length));
            AssertSeededItemsPreserved(items);
            foreach (int titleId in extraTitleIds)
            {
                var existing = items.Single(item => item.TitleId == titleId);
                existing.Note.ShouldBe($"Seeded {titleId}");
                existing.AddedAt.ShouldBe(AddedAt);
            }
            if (!remove)
                items.Single(item => item.TitleId == 22).Note.ShouldBe("later note");
        }
        finally
        {
            gate.Release.TrySetResult();
        }

        var result = await addition;
        result.IsError.ShouldBeFalse();
        result.Value.TitleId.ShouldBe(IdCoder.Encode(22));
        result.Value.TitleName.ShouldBe("Title 22");
        result.Value.Note.ShouldBe("accepted note");
        result.Value.SortOrder.ShouldBe(2 + extraTitleIds.Length);
        result.Value.AddedAt.ShouldBeGreaterThan(AddedAt);
    }

    [Fact]
    public async Task UpdateItemNote_MissingCollection_ReturnsNotFound()
    {
        await using var context = _database.CreateContext();

        var result = await NoteHandler(context, Repository(context))
            .HandleAsync(new UpdateCollectionItemNoteCommand(999, 20, null));

        result.FirstError.Code.ShouldBe("Collections.NotFound");
    }

    [Fact]
    public async Task LockForItemMutationStagedAsync_WithoutTransaction_Throws()
    {
        await SeedAsync();
        await using var context = _database.CreateContext();

        await Should.ThrowAsync<InvalidOperationException>(
            Repository(context).LockForItemMutationStagedAsync(CollectionId));
    }

    /// <summary>
    ///     Runs <paramref name="holder"/> until it has locked the collection, starts
    ///     <paramref name="contender"/> on an independent context, and releases the holder once
    ///     the contender waits on a PostgreSQL lock.
    /// </summary>
    private async Task<(ErrorOr<THolder> Holder, ErrorOr<TContender> Contender)> InterleaveAsync<THolder, TContender>(
        Func<RomdDbContext, ICollectionRepository, CancellationToken, Task<ErrorOr<THolder>>> holder,
        Func<RomdDbContext, ICollectionRepository, CancellationToken, Task<ErrorOr<TContender>>> contender,
        CancellationToken ct)
    {
        await using var holderContext = _database.CreateContext();
        await using var contenderContext = _database.CreateContext();
        await contenderContext.Database.OpenConnectionAsync(ct);
        int contenderPid = ((Npgsql.NpgsqlConnection)contenderContext.Database.GetDbConnection()).ProcessID;
        var gate = new Gate(GatePoint.AfterLock);

        var holderTask = holder(holderContext, new GatedCollectionRepository(Repository(holderContext), gate), ct);
        await gate.Reached.Task.WaitAsync(ct);
        var contenderTask = contender(contenderContext, Repository(contenderContext), ct);
        using var stopObserving = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var lockWait = WaitForLockWaitAsync(contenderPid, stopObserving.Token);
        var observed = await Task.WhenAny(contenderTask, lockWait);
        await stopObserving.CancelAsync();
        gate.Release.TrySetResult();

        var holderResult = await holderTask;
        var contenderResult = await contenderTask;
        observed.ShouldBe(lockWait, "The overlapping request must wait for the collection lock.");
        return (holderResult, contenderResult);
    }

    private async Task WaitForLockWaitAsync(int pid, CancellationToken ct)
    {
        // Observe PostgreSQL's lock queue instead of assuming scheduling from elapsed time.
        await using var observer = _database.CreateContext();
        while (!await observer.Database.SqlQuery<bool>(
                   $"SELECT EXISTS (SELECT 1 FROM pg_locks WHERE pid = {pid} AND NOT granted) AS \"Value\"")
                   .SingleAsync(ct))
        {
            await Task.Yield();
        }
    }

    private static AddTitleToCollectionCommandHandler AddHandler(RomdDbContext context, ICollectionRepository repository) =>
        new(Catalog(context), repository, new TitleReadRepository(context), new EfUnitOfWork(context), TimeProvider.System,
            NullLogger<AddTitleToCollectionCommandHandler>.Instance);

    private static RemoveTitleFromCollectionCommandHandler RemoveHandler(RomdDbContext context, ICollectionRepository repository) =>
        new(Catalog(context), repository, new EfUnitOfWork(context),
            NullLogger<RemoveTitleFromCollectionCommandHandler>.Instance);

    private static ReorderCollectionItemsCommandHandler ReorderHandler(RomdDbContext context, ICollectionRepository repository) =>
        new(Catalog(context), repository, new EfUnitOfWork(context),
            NullLogger<ReorderCollectionItemsCommandHandler>.Instance);

    private static UpdateCollectionItemNoteCommandHandler NoteHandler(RomdDbContext context, ICollectionRepository repository) =>
        new(Catalog(context), repository, new EfUnitOfWork(context),
            NullLogger<UpdateCollectionItemNoteCommandHandler>.Instance);

    private static UpdateCollectionCommandHandler DetailsHandler(RomdDbContext context, ICollectionRepository repository) =>
        new(Catalog(context), repository, new Romd.Persistence.Queries.CollectionCoverMediaReader(context), new EfUnitOfWork(context),
            NullLogger<UpdateCollectionCommandHandler>.Instance);

    private static ReferenceCatalogService Catalog(RomdDbContext context) =>
        new(context, TestReferenceBlobs.For(context));

    private static CollectionRepository Repository(RomdDbContext context) =>
        new(context, Substitute.For<IConsumerReleaseSelector>());

    private async Task<CollectionEntity> LoadAsync(CancellationToken ct)
    {
        await using var context = _database.CreateContext();
        return await context.Collections.Include(c => c.Items).SingleAsync(c => c.Id == CollectionId, ct);
    }

    private static List<CollectionItemEntity> Ordered(CollectionEntity collection) =>
        collection.Items.OrderBy(i => i.SortOrder).ThenBy(i => i.TitleId).ToList();

    private static void AssertCollectionPropertiesPreserved(CollectionEntity collection, string name)
    {
        collection.Name.ShouldBe(name);
        collection.IsSystem.ShouldBeTrue();
        collection.SortOrder.ShouldBe(7);
        collection.CreatedAt.ShouldBe(AddedAt);
    }

    private static void AssertSeededItemsPreserved(IReadOnlyList<CollectionItemEntity> items)
    {
        var first = items.Single(i => i.TitleId == 21);
        first.Id.ShouldBe(40);
        first.Note.ShouldBe("Play first");
        first.AddedAt.ShouldBe(AddedAt);
        var second = items.Single(i => i.TitleId == 20);
        second.Id.ShouldBe(41);
        second.Note.ShouldBe("Play next");
        second.AddedAt.ShouldBe(AddedAt.AddDays(1));
    }

    private async Task SeedAsync()
    {
        await using var context = _database.CreateContext();
        context.Platforms.Add(new PlatformEntity
        {
            Id = 10,
            Name = "NES",
            Ownership = Romd.Domain.ReferenceData.ReferenceOwnership.Romd,
            BuiltInVersion = 1,
            BaseName = "NES",
            BaseCompactLabel = "NES",
            CanonicalKey = "nes",
            ShortName = "nes",
            CreatedAt = AddedAt
        });
        context.Titles.AddRange(Enumerable.Range(20, 4).Select(id => new TitleEntity
        {
            Id = id,
            PlatformId = 10,
            Name = $"Title {id}",
            NormalizedName = $"title {id}",
            EnrichmentStatus = "NotEnriched",
            CreatedAt = AddedAt
        }));
        context.Collections.Add(new CollectionEntity
        {
            Id = CollectionId,
            Name = "Original",
            Description = "Original description",
            IsSystem = true,
            SortOrder = 7,
            CreatedAt = AddedAt,
            Items =
            [
                new CollectionItemEntity
                {
                    Id = 40, CollectionId = CollectionId, TitleId = 21, SortOrder = 0,
                    Note = "Play first", AddedAt = AddedAt, CreatedAt = AddedAt
                },
                new CollectionItemEntity
                {
                    Id = 41, CollectionId = CollectionId, TitleId = 20, SortOrder = 1,
                    Note = "Play next", AddedAt = AddedAt.AddDays(1), CreatedAt = AddedAt
                }
            ]
        });
        await context.SaveChangesAsync();
    }

    private async Task<int[]> SeedAdditionalItemsAsync(CancellationToken ct)
    {
        int[] titleIds = [.. Enumerable.Range(100, 51)];
        await using var context = _database.CreateContext();
        context.Titles.AddRange(titleIds.Select(id => new TitleEntity
        {
            Id = id,
            PlatformId = 10,
            Name = $"Title {id}",
            NormalizedName = $"title {id}",
            EnrichmentStatus = "NotEnriched",
            CreatedAt = AddedAt
        }));
        context.CollectionItems.AddRange(titleIds.Select((titleId, index) => new CollectionItemEntity
        {
            Id = titleId,
            CollectionId = CollectionId,
            TitleId = titleId,
            SortOrder = index + 2,
            Note = $"Seeded {titleId}",
            AddedAt = AddedAt,
            CreatedAt = AddedAt
        }));
        await context.SaveChangesAsync(ct);
        return titleIds;
    }

    public void Dispose() => _database.Dispose();

    private enum GatePoint
    {
        AfterLock,
        AfterAddStaged,
        AfterCommit
    }

    private sealed class Gate(GatePoint point)
    {
        public GatePoint Point { get; } = point;
        public TaskCompletionSource Reached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task PassAsync(GatePoint point, CancellationToken ct)
        {
            if (point != Point)
                return;
            Reached.TrySetResult();
            await Release.Task.WaitAsync(ct);
        }
    }

    private sealed class FailingCommitInterceptor : DbTransactionInterceptor
    {
        public override ValueTask<InterceptionResult> TransactionCommittingAsync(
            DbTransaction transaction,
            TransactionEventData eventData,
            InterceptionResult result,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Simulated commit failure.");
    }

    private sealed class CommittedGateInterceptor(Gate gate) : DbTransactionInterceptor
    {
        public override Task TransactionCommittedAsync(
            DbTransaction transaction,
            TransactionEndEventData eventData,
            CancellationToken cancellationToken = default) =>
            gate.PassAsync(GatePoint.AfterCommit, cancellationToken);
    }

    /// <summary>Pauses a request at a chosen point while it holds the collection lock.</summary>
    private sealed class GatedCollectionRepository(ICollectionRepository inner, Gate gate) : ICollectionRepository
    {
        public Task<ErrorOr<Romd.Application.Common.Pagination.PagedList<CollectionSummaryReadModel>>> GetPageAsync(int? platformId, string? query, string? cursor, int limit, CancellationToken ct) => inner.GetPageAsync(platformId, query, cursor, limit, ct);
        public Task<ErrorOr<Romd.Application.Common.Pagination.PagedList<CollectionItemReadModel>>> GetItemPageAsync(int collectionId, string? cursor, int limit, CancellationToken ct) => inner.GetItemPageAsync(collectionId, cursor, limit, ct);
        public Task<CollectionItemReadModel?> GetItemDetailAsync(int collectionId, int titleId, CancellationToken ct) => inner.GetItemDetailAsync(collectionId, titleId, ct);
        public Task<int> CountItemsAsync(int collectionId, CancellationToken ct) => inner.CountItemsAsync(collectionId, ct);

        public async Task<Collection?> LockForItemMutationStagedAsync(int collectionId, CancellationToken ct = default)
        {
            var collection = await inner.LockForItemMutationStagedAsync(collectionId, ct);
            await gate.PassAsync(GatePoint.AfterLock, ct);
            return collection;
        }

        public async Task AddItemStagedAsync(CollectionItem item, CancellationToken ct = default)
        {
            await inner.AddItemStagedAsync(item, ct);
            await gate.PassAsync(GatePoint.AfterAddStaged, ct);
        }

        public Task RemoveItemStagedAsync(int collectionId, int titleId, CancellationToken ct = default) =>
            inner.RemoveItemStagedAsync(collectionId, titleId, ct);

        public Task UpdateItemNoteStagedAsync(CollectionItem item, CancellationToken ct = default) =>
            inner.UpdateItemNoteStagedAsync(item, ct);

        public Task ApplyItemOrderStagedAsync(int collectionId, IReadOnlyCollection<CollectionItem> items, CancellationToken ct = default) =>
            inner.ApplyItemOrderStagedAsync(collectionId, items, ct);

        public Task<Collection?> GetByIdAsync(int id, CancellationToken ct = default) => inner.GetByIdAsync(id, ct);

        public Task<Collection?> GetWithItemsAsync(int id, CancellationToken ct = default) => inner.GetWithItemsAsync(id, ct);

        public Task<IReadOnlyList<CollectionSummaryReadModel>> GetAllAsync(int? platformId = null, CancellationToken ct = default) =>
            inner.GetAllAsync(platformId, ct);

        public Task<IReadOnlyList<CollectionItemReadModel>> GetItemDetailsAsync(int collectionId, CancellationToken ct = default) =>
            inner.GetItemDetailsAsync(collectionId, ct);

        public Task<IStagedCollectionCreation> AddStagedAsync(Collection collection, CancellationToken ct = default) => inner.AddStagedAsync(collection, ct);

        public Task<CollectionSummaryReadModel?> UpdateDetailsStagedAsync(Collection collection, CancellationToken ct = default) =>
            inner.UpdateDetailsStagedAsync(collection, ct);

        public Task DeleteAsync(int id, CancellationToken ct = default) => inner.DeleteAsync(id, ct);
    }
}
