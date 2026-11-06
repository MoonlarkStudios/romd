using System.Security.Cryptography;
using System.Text;
using ErrorOr;
using Microsoft.Extensions.Logging.Abstractions;
using Romd.Contracts.Consumer.Account;
using Microsoft.EntityFrameworkCore;
using Romd.Consumer.Application.Vfs;
using Romd.Domain.Vfs;
using Romd.Persistence;
using Romd.Persistence.Identity;
using Romd.Persistence.Entities;
using Romd.PostgreSql.TestSupport;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Persistence;

public sealed class VfsConnectionStoreTests : IDisposable
{
    private readonly PostgreSqlTestDatabase _database = PostgreSqlTestDatabase.Create();
    private readonly Guid _user = Guid.NewGuid();
    private readonly Guid _other = Guid.NewGuid();

    public VfsConnectionStoreTests()
    {
        using var db = CreateDb();
        db.Users.AddRange(new RomdUser { Id = _user, UserName = "owner", SecurityStamp = "stamp" },
            new RomdUser { Id = _other, UserName = "other", SecurityStamp = "other-stamp" });
        db.SaveChanges();
    }

    [Fact]
    public async Task Issue_StoresOnlyVerifier_AndEnforcesOwnership()
    {
        await using var db = CreateDb(guard: true);
        var store = new VfsConnectionStore(db, TimeProvider.System);
        var issued = (await CreateAsync(db, store, _user, "Steam Deck", default)).Value;
        issued.Password.Length.ShouldBe(43);
        var row = await db.VfsConnections.AsNoTracking().SingleAsync();
        row.PasswordVerifier.ShouldBe(SHA256.HashData(Encoding.UTF8.GetBytes(issued.Password)));
        var authenticated = (await store.AuthenticateAsync(issued.Connection.Username, issued.Password, default))!;
        authenticated.UserId.ShouldBe(_user);
        authenticated.ConnectionId.ShouldBe(issued.Connection.Id);
        (await store.AuthenticateAsync(issued.Connection.Username, new string('a', 43), default)).ShouldBeNull();
        (await store.ListAsync(_other, default)).ShouldBeEmpty();
        (await store.RenameAsync(_other, row.Id, "stolen", default)).IsError.ShouldBeTrue();
        (await RotateAsync(db, store, _other, row.Id, default)).IsError.ShouldBeTrue();
        (await RevokeAsync(db, store, _other, row.Id, default)).IsError.ShouldBeTrue();
        (await store.ListAsync(_user, default)).Single().LastUsedAt.ShouldNotBeNull();
    }

    [Fact]
    public async Task Format_UpdateIsCurrentPerConnectionAndSurvivesRenameAndRotation()
    {
        await using var db = CreateDb(guard: true);
        var store = new VfsConnectionStore(db, TimeProvider.System);
        var stored = (await CreateAsync(db, store, _user, "Deck", default)).Value;
        var original = (await CreateAsync(db, store, _user, "Console", default, VfsFormat.Original)).Value;
        stored.Connection.Format.ShouldBe("stored");
        original.Connection.Format.ShouldBe("original");
        (await store.AuthenticateAsync(stored.Connection.Username, stored.Password, default))!.Format.ShouldBe(VfsFormat.Stored);
        var updated = (await store.RenameAsync(_user, stored.Connection.Id, "Deck updated", default, VfsFormat.Original)).Value;
        updated.Format.ShouldBe("original");
        (await store.AuthenticateAsync(stored.Connection.Username, stored.Password, default))!.Format.ShouldBe(VfsFormat.Original);
        (await store.RenameAsync(_user, stored.Connection.Id, "Deck renamed", default)).Value.Format.ShouldBe("original");
        (await store.RenameAsync(_other, original.Connection.Id, "Stolen", default, VfsFormat.Stored)).FirstError.ShouldBe(VfsConnectionErrors.NotFound());
        (await store.AuthenticateAsync(original.Connection.Username, original.Password, default))!.Format.ShouldBe(VfsFormat.Original);
        var rotated = (await RotateAsync(db, store, _user, stored.Connection.Id, default)).Value;
        rotated.Connection.Format.ShouldBe("original");
        (await store.AuthenticateAsync(rotated.Connection.Username, rotated.Password, default))!.Format.ShouldBe(VfsFormat.Original);
        await RevokeAsync(db, store, _user, rotated.Connection.Id, default);
        (await store.AuthenticateAsync(rotated.Connection.Username, rotated.Password, default)).ShouldBeNull();
    }

    [Fact]
    public async Task Format_InvalidTypedSelection_DoesNotMutateCredentialOrSetting()
    {
        await using var db = CreateDb(guard: true);
        var store = new VfsConnectionStore(db, TimeProvider.System);
        (await CreateAsync(db, store, _user, "Invalid", default, (VfsFormat)99)).FirstError.ShouldBe(VfsConnectionErrors.InvalidFormat());
        var issued = (await CreateAsync(db, store, _user, "Deck", default)).Value;
        (await store.RenameAsync(_user, issued.Connection.Id, "Invalid", default, (VfsFormat)99)).FirstError.ShouldBe(VfsConnectionErrors.InvalidFormat());
        var row = (await store.ListAsync(_user, default)).ShouldHaveSingleItem();
        row.Name.ShouldBe("Deck");
        row.Format.ShouldBe("stored");
        (await store.AuthenticateAsync(row.Username, issued.Password, default))!.Format.ShouldBe(VfsFormat.Stored);
    }

    [Fact]
    public async Task RotateAndRevoke_InvalidatePreviousCopies_WithoutResurrection()
    {
        await using var db = CreateDb(guard: true);
        var store = new VfsConnectionStore(db, TimeProvider.System);
        var first = (await CreateAsync(db, store, _user, "Deck", default)).Value;
        var second = (await RotateAsync(db, store, _user, first.Connection.Id, default)).Value;
        second.Password.ShouldNotBe(first.Password);
        (await store.AuthenticateAsync(first.Connection.Username, first.Password, default)).ShouldBeNull();
        (await store.AuthenticateAsync(second.Connection.Username, second.Password, default))!.UserId.ShouldBe(_user);
        (await RevokeAsync(db, store, _user, first.Connection.Id, default)).IsError.ShouldBeFalse();
        db.ChangeTracker.Clear();
        (await RotateAsync(db, store, _user, first.Connection.Id, default)).IsError.ShouldBeTrue();
        (await store.AuthenticateAsync(second.Connection.Username, second.Password, default)).ShouldBeNull();
        (await store.ListAsync(_user, default)).Single().Status.ShouldBe("Revoked");
    }

    [Theory]
    [InlineData("stamp")]
    [InlineData("suspended")]
    [InlineData("activation")]
    public async Task Authenticate_AccountInvalidated_DeniesExistingCredential(string reason)
    {
        await using var db = CreateDb();
        var store = new VfsConnectionStore(db, TimeProvider.System);
        var issued = (await CreateAsync(db, store, _user, "Deck", default)).Value;
        var user = await db.Users.AsTracking().SingleAsync(user => user.Id == _user);
        if (reason == "stamp")
            user.SecurityStamp = "changed";
        if (reason == "suspended")
            user.IsSuspended = true;
        if (reason == "activation")
            user.RequiresActivation = true;
        await db.SaveChangesAsync();
        (await store.AuthenticateAsync(issued.Connection.Username, issued.Password, default)).ShouldBeNull();
        (await store.ListAsync(_user, default)).Single().Status.ShouldBe("Invalidated");
    }

    [Fact]
    public async Task Create_AtActiveLimit_RequiresRevocationBeforeAnotherCredential()
    {
        await using var db = CreateDb();
        var store = new VfsConnectionStore(db, TimeProvider.System);
        for (int index = 0; index < 32; index++)
            (await CreateAsync(db, store, _user, $"Device {index}", default)).IsError.ShouldBeFalse();
        (await CreateAsync(db, store, _user, "Too many", default)).IsError.ShouldBeTrue();
        var first = (await store.ListAsync(_user, default))[0];
        await RevokeAsync(db, store, _user, first.Id, default);
        (await CreateAsync(db, store, _user, "Replacement", default)).IsError.ShouldBeFalse();
    }

    private RomdDbContext CreateDb(bool guard = false)
    {
        var options = new DbContextOptionsBuilder<RomdDbContext>().UseNpgsql(_database.ConnectionString)
            .UseQueryTrackingBehavior(QueryTrackingBehavior.NoTracking);
        if (guard)
            options.AddInterceptors(new ConsumerWriteGuardInterceptor());
        return new RomdDbContext(options.Options);
    }

    [Fact]
    public async Task ListPage_TraversesEntireTiedHistoryAndRejectsForeignOrMalformedCursor()
    {
        await using var db = CreateDb();
        var createdAt = DateTimeOffset.Parse("2026-10-01T10:00:00Z");
        db.VfsConnections.AddRange(Enumerable.Range(1, 205).Select(index => new VfsConnectionEntity
        {
            Id = Guid.Parse($"00000000-0000-4000-8000-{index:D12}"),
            UserId = _user,
            Name = "History " + index,
            CreatedAt = createdAt,
            CredentialUpdatedAt = createdAt,
            RevokedAt = createdAt,
            AccountStamp = "stamp",
            PasswordVerifier = new byte[32]
        }));
        await db.SaveChangesAsync();
        var store = new VfsConnectionStore(db, TimeProvider.System);
        var ids = new List<Guid>();
        string? cursor = null;
        string? firstCursor = null;
        do
        {
            var result = await store.ListPageAsync(_user, cursor, 100, default);
            result.IsError.ShouldBeFalse();
            ids.AddRange(result.Value.Items.Select(item => item.Id));
            cursor = result.Value.NextCursor;
            firstCursor ??= cursor;
            result.Value.HasNextPage.ShouldBe(cursor is not null);
        } while (cursor is not null);

        ids.Count.ShouldBe(205);
        ids.Distinct().Count().ShouldBe(205);
        (await store.ListPageAsync(_other, firstCursor, 100, default)).FirstError.Code.ShouldBe("Pagination.InvalidCursor");
        (await store.ListPageAsync(_user, "malformed", 100, default)).FirstError.Code.ShouldBe("Pagination.InvalidCursor");
    }

    [Fact]
    public async Task Patch_FormatOnlyPreservesNameAndIssuedCredential()
    {
        await using var db = CreateDb(guard: true);
        var store = new VfsConnectionStore(db, TimeProvider.System);
        var issued = (await CreateAsync(db, store, _user, "Deck", default)).Value;

        var patched = await store.RenameAsync(_user, issued.Connection.Id, null, default, VfsFormat.Original);

        patched.IsError.ShouldBeFalse();
        patched.Value.Name.ShouldBe("Deck");
        patched.Value.Format.ShouldBe("original");
        (await store.AuthenticateAsync(issued.Connection.Username, issued.Password, default))!.Format.ShouldBe(VfsFormat.Original);
    }

    private static Task<ErrorOr<IssuedVfsCredential>> CreateAsync(RomdDbContext db, VfsConnectionStore store,
        Guid userId, string name, CancellationToken ct, VfsFormat format = VfsFormat.Stored) =>
        Transaction(db).ExecuteAsync(token => store.CreateAsync(userId, name, token, format), ct);

    private static Task<ErrorOr<IssuedVfsCredential>> RotateAsync(RomdDbContext db, VfsConnectionStore store,
        Guid userId, Guid id, CancellationToken ct) =>
        Transaction(db).ExecuteAsync(token => store.RotateAsync(userId, id, token), ct);

    private static Task<ErrorOr<Success>> RevokeAsync(RomdDbContext db, VfsConnectionStore store,
        Guid userId, Guid id, CancellationToken ct) =>
        Transaction(db).ExecuteAsync(token => store.RevokeAsync(userId, id, token), ct);

    private static ConsumerSecurityTransaction Transaction(RomdDbContext db) =>
        new(new EfUnitOfWork(db), NullLogger<ConsumerSecurityTransaction>.Instance);

    public void Dispose() => _database.Dispose();
}
