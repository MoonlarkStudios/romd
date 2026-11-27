using ErrorOr;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Romd.Application.Common.Pagination;
using Romd.Application.Common.Security;
using Romd.Consumer.Application.Browse;
using Romd.Consumer.Application.Browse.ReadModels;
using Romd.Consumer.Application.Libraries;
using Romd.Consumer.Application.PlayLater.Commands.AddPlayLaterTitle;
using Romd.Domain.Libraries;
using Romd.Domain.ReferenceData;
using Romd.Persistence;
using Romd.Persistence.Entities;
using Romd.Persistence.Identity;
using Romd.Persistence.Repositories;
using Romd.PostgreSql.TestSupport;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Persistence;

public sealed class PlayLaterRepositoryTests : IAsyncLifetime
{
    private static readonly ConsumerLibraryScope Account = new(Guid.Parse("091b0328-a406-4174-92ef-982caf314c88"));
    private static readonly ConsumerLibraryScope OtherAccount = new(Guid.Parse("53701159-82bc-46de-bfa2-8ecf35db24e0"));
    private static readonly DateTimeOffset AddedAt = new(2026, 10, 10, 12, 0, 0, TimeSpan.Zero);
    private readonly PostgreSqlTestDatabase _database = PostgreSqlTestDatabase.Create();
    private const int PlatformId = 91;
    private const int SourceTitleId = 101;
    private const int TargetTitleId = 102;
    private const int ThirdTitleId = 103;

    [Fact]
    public async Task ListPlayLaterTitles_HidesUnpublishedEntriesBeforePagingAndIsolatesAccounts()
    {
        await using var admin = _database.CreateContext();
        await using var consumer = ConsumerContext();
        var repository = Repository(consumer);
        await SetMembershipAsync(consumer, Account, SourceTitleId, AddedAt);
        await SetMembershipAsync(consumer, Account, TargetTitleId, AddedAt);
        await SetMembershipAsync(consumer, Account, ThirdTitleId, AddedAt.AddMinutes(1));
        await SetMembershipAsync(consumer, OtherAccount, TargetTitleId, AddedAt);
        await SetMembershipAsync(consumer, Account, SourceTitleId, AddedAt.AddMinutes(2));
        await admin.MaterializedLibraryTitles.Where(title => title.TitleId == ThirdTitleId)
            .ExecuteUpdateAsync(set => set.SetProperty(title => title.IsOwned, false));

        var first = await ListAsync(repository, Account, limit: 1);
        first.Items.ShouldHaveSingleItem().Id.ShouldBe(TargetTitleId);
        first.HasNextPage.ShouldBeTrue();
        var last = await ListAsync(repository, Account, limit: 1, cursor: first.NextCursor);
        last.Items.ShouldHaveSingleItem().Id.ShouldBe(SourceTitleId);
        last.HasNextPage.ShouldBeFalse();
        last.NextCursor.ShouldBeNull();
        (await repository.ListPlayLaterTitlesAsync(OtherAccount, ConsumerReleasePreference.Default, first.NextCursor, 1))
            .ShouldBeOfType<ConsumerLibraryReadResult<PagedList<ConsumerTitleCardData>>.Rejected>();
        (await ListAsync(repository, OtherAccount)).Items.ShouldHaveSingleItem().Id.ShouldBe(TargetTitleId);
        (await MembershipAsync(repository, Account, SourceTitleId, TargetTitleId, ThirdTitleId, 999))
            .ShouldBe([SourceTitleId, TargetTitleId]);

        (await SetMembershipAsync(consumer, Account, ThirdTitleId, AddedAt.AddMinutes(3))).ShouldBeFalse();
        (await SetMembershipAsync(consumer, Account, ThirdTitleId, null)).ShouldBeFalse();
        await admin.MaterializedLibraryTitles.Where(title => title.TitleId == ThirdTitleId)
            .ExecuteUpdateAsync(set => set.SetProperty(title => title.IsOwned, true));
        (await ListAsync(repository, Account)).Items.Select(title => title.Id)
            .ShouldBe([ThirdTitleId, TargetTitleId, SourceTitleId]);
    }

    [Fact]
    public async Task SetPlayLaterMembership_ConcurrentRetries_PreserveFirstAdditionAndNoDuplicateRows()
    {
        await using var firstConsumer = ConsumerContext();
        await using var secondConsumer = ConsumerContext();

        (await Task.WhenAll(
            SetMembershipAsync(firstConsumer, Account, SourceTitleId, AddedAt),
            SetMembershipAsync(secondConsumer, Account, SourceTitleId, AddedAt))).ShouldBe([true, true]);
        (await SetMembershipAsync(secondConsumer, Account, SourceTitleId, AddedAt.AddMinutes(1))).ShouldBeTrue();
        (await firstConsumer.PlayLaterEntries.SingleAsync()).AddedAt.ShouldBe(AddedAt);

        (await Task.WhenAll(
            SetMembershipAsync(firstConsumer, Account, SourceTitleId, null),
            SetMembershipAsync(secondConsumer, Account, SourceTitleId, null))).ShouldBe([true, true]);
        (await firstConsumer.PlayLaterEntries.AnyAsync()).ShouldBeFalse();
    }

    [Fact]
    public async Task AddPlayLaterTitle_AbortedTransaction_RollsBackTheStagedEntryAndAllowsRetry()
    {
        await using var consumer = ConsumerContext();
        var repository = Repository(consumer);
        var unitOfWork = new EfUnitOfWork(consumer);
        var transaction = new PlayLaterTransaction(unitOfWork, NullLogger<PlayLaterTransaction>.Instance);

        var rejected = await transaction.ExecuteAsync<Success>(async ct =>
        {
            (await repository.StagePlayLaterMembershipAsync(Account, SourceTitleId, AddedAt, ct)).ShouldBeTrue();
            await unitOfWork.FlushAsync(ct);
            (await consumer.PlayLaterEntries.AnyAsync(ct)).ShouldBeTrue();
            return Error.Conflict("PlayLater.InterruptedTestMutation");
        });

        rejected.FirstError.Code.ShouldBe("PlayLater.InterruptedTestMutation");
        await using var observer = _database.CreateContext();
        (await observer.PlayLaterEntries.AnyAsync()).ShouldBeFalse();

        var user = Substitute.For<ICurrentUser>();
        user.IsAuthenticated.Returns(true);
        user.UserId.Returns(Account.UserId);
        var handler = new AddPlayLaterTitleCommandHandler(user, repository, transaction, TimeProvider.System);
        (await handler.HandleAsync(new(new(SourceTitleId)))).IsError.ShouldBeFalse();
        var saved = await observer.PlayLaterEntries.SingleAsync();
        (saved.UserId, saved.TitleId).ShouldBe((Account.UserId, SourceTitleId));
    }

    [Fact]
    public async Task MergeTitles_OverlappingLists_CollapseDuplicatesAndTransferSourceOnlyEntries()
    {
        await using var admin = _database.CreateContext();
        await using var consumer = ConsumerContext();
        var repository = Repository(consumer);
        await SetMembershipAsync(consumer, Account, SourceTitleId, AddedAt.AddMinutes(2));
        await SetMembershipAsync(consumer, Account, TargetTitleId, AddedAt);
        await SetMembershipAsync(consumer, Account, ThirdTitleId, AddedAt.AddMinutes(1));
        await SetMembershipAsync(consumer, OtherAccount, SourceTitleId, AddedAt);

        await using (var transaction = await admin.Database.BeginTransactionAsync())
        {
            await StageMergeAsync(admin, default);
            await transaction.CommitAsync();
        }

        (await ListAsync(repository, Account)).Items.Select(title => title.Id).ShouldBe([TargetTitleId, ThirdTitleId]);
        var transferred = await admin.PlayLaterEntries.SingleAsync(entry => entry.UserId == OtherAccount.UserId);
        transferred.TitleId.ShouldBe(TargetTitleId);
        transferred.AddedAt.ShouldBe(AddedAt);
        (await MembershipAsync(repository, Account, SourceTitleId, TargetTitleId)).ShouldBe([TargetTitleId]);

        (await SetMembershipAsync(consumer, Account, SourceTitleId, AddedAt.AddMinutes(3))).ShouldBeTrue();
        var merged = await admin.PlayLaterEntries.SingleAsync(entry =>
            entry.UserId == Account.UserId && entry.TitleId == TargetTitleId);
        merged.AddedAt.ShouldBe(AddedAt.AddMinutes(2));
        (await SetMembershipAsync(consumer, Account, SourceTitleId, null)).ShouldBeTrue();
        (await MembershipAsync(repository, Account, TargetTitleId)).ShouldBeEmpty();
        (await MembershipAsync(repository, OtherAccount, TargetTitleId)).ShouldBe([TargetTitleId]);
    }

    [Fact]
    public async Task SetPlayLaterMembership_MergeInProgress_WaitsAndSavesTheSurvivingTitle()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using var admin = _database.CreateContext();
        await using var transaction = await admin.Database.BeginTransactionAsync(deadline.Token);
        await StageMergeAsync(admin, deadline.Token);

        await using var consumer = ConsumerContext();
        var (pending, waited) = await _database.StartWaitingForALockAsync(consumer,
            ct => SetMembershipAsync(consumer, Account, SourceTitleId, AddedAt, ct), deadline.Token);
        Exception? primary = null;
        try
        {
            waited.ShouldBeTrue("A Play Later write must wait before resolving the source title of the merge.");
            await transaction.CommitAsync(deadline.Token);
            (await pending).ShouldBeTrue();
            var entry = await admin.PlayLaterEntries.SingleAsync(deadline.Token);
            entry.TitleId.ShouldBe(TargetTitleId);
            entry.AddedAt.ShouldBe(AddedAt);
        }
        catch (Exception error)
        {
            primary = error;
            throw;
        }
        finally
        {
            await PostgreSqlLockQueue.CancelAndJoinAsync(deadline, primary, pending);
        }
    }

    public async Task InitializeAsync()
    {
        await using var db = _database.CreateContext();
        var library = LibraryEntity.FromDomain(Library.CreateNew("Play Later Library", new LibraryConfiguration(), now: AddedAt));
        library.NeedsMaterialization = false;
        library.LastMaterializedAt = AddedAt;
        db.Libraries.Add(library);
        db.Platforms.Add(new PlatformEntity
        {
            Id = PlatformId,
            Name = "Test Platform",
            ShortName = "play-later-test",
            CanonicalKey = "play-later-test",
            Ownership = ReferenceOwnership.Romd,
            BuiltInVersion = 1,
            BaseName = "Test Platform",
            BaseCompactLabel = "Test Platform",
            CreatedAt = AddedAt
        });
        await db.SaveChangesAsync();

        db.Users.AddRange(
            new RomdUser { Id = Account.UserId, UserName = "player", LibraryId = library.Id, CreatedAt = AddedAt },
            new RomdUser { Id = OtherAccount.UserId, UserName = "other", LibraryId = library.Id, CreatedAt = AddedAt });
        foreach (int titleId in new[] { SourceTitleId, TargetTitleId, ThirdTitleId })
        {
            db.Titles.Add(new TitleEntity
            {
                Id = titleId,
                PlatformId = PlatformId,
                Name = $"Title {titleId}",
                NormalizedName = $"title {titleId}",
                EnrichmentStatus = "None",
                CreatedAt = AddedAt
            });
            db.MaterializedLibraryTitles.Add(new MaterializedLibraryTitleEntity
            {
                LibraryId = library.Id,
                TitleId = titleId,
                PlatformId = PlatformId,
                IsOwned = true,
                IsVisible = true,
                Availability = LibraryTitleAvailability.Playable.ToString()
            });
        }
        await db.SaveChangesAsync();
    }

    public Task DisposeAsync() => _database.DisposeAsync().AsTask();

    private RomdDbContext ConsumerContext() => new(_database.CreateOptions(
        options => options.AddInterceptors(new ConsumerWriteGuardInterceptor())));

    private static ConsumerBrowseRepository Repository(RomdDbContext context) => new(context, new ConsumerReleaseSelector());

    private static async Task<bool> SetMembershipAsync(
        RomdDbContext context, ConsumerLibraryScope scope, int titleId, DateTimeOffset? addedAt,
        CancellationToken ct = default)
    {
        var transaction = new PlayLaterTransaction(new EfUnitOfWork(context), NullLogger<PlayLaterTransaction>.Instance);
        var result = await transaction.ExecuteAsync<bool>(async token =>
            await Repository(context).StagePlayLaterMembershipAsync(scope, titleId, addedAt, token), ct);
        return result.Value;
    }

    private static async Task<PagedList<ConsumerTitleCardData>> ListAsync(
        ConsumerBrowseRepository repository, ConsumerLibraryScope scope, int limit = 100, string? cursor = null) =>
        (await repository.ListPlayLaterTitlesAsync(scope, ConsumerReleasePreference.Default, cursor, limit))
        .ShouldBeOfType<ConsumerLibraryReadResult<PagedList<ConsumerTitleCardData>>.Found>().Value;

    private static async Task<IReadOnlyList<int>> MembershipAsync(
        ConsumerBrowseRepository repository, ConsumerLibraryScope scope, params int[] titleIds) =>
        (await repository.GetPlayLaterMembershipAsync(scope, titleIds))
        .ShouldBeOfType<ConsumerLibraryReadResult<IReadOnlyList<int>>.Found>().Value;

    private static async Task StageMergeAsync(RomdDbContext context, CancellationToken ct)
    {
        var repository = new TitleRepository(context);
        await repository.ReassignTitleReferencesAsync(SourceTitleId, TargetTitleId, ct);
        await repository.DeleteAbsorbedSourceAsync(SourceTitleId, ct);
        await context.SaveChangesAsync(ct);
    }
}
