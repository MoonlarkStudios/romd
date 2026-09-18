using Microsoft.EntityFrameworkCore;
using Romd.Persistence.Identity;
using Romd.PostgreSql.TestSupport;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Identity;

public sealed class AccountSessionStoreTests
{
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 9, 10, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    [Fact]
    public async Task Activity_ThrottlesWrites_AndExpirationCannotBeExtendedByUse()
    {
        await using var database = PostgreSqlTestDatabase.Create();
        await using var db = database.CreateContext();
        var user = new RomdUser { UserName = "session", SecurityStamp = "stamp" };
        db.Users.Add(user); await db.SaveChangesAsync();
        var clock = new Clock(); var store = new AccountSessionStore(db, clock);
        var session = await store.StartAsync(user.Id, "client", "Firefox on Linux", "stamp", default);
        clock.Now += TimeSpan.FromMinutes(4);
        (await store.ValidateAsync(user.Id, session.Id, "stamp", default)).ShouldNotBeNull();
        (await store.ListAsync(user.Id, default)).Single().LastUsedAt.ShouldBe(session.CreatedAt);
        clock.Now += TimeSpan.FromMinutes(2);
        await store.ValidateAsync(user.Id, session.Id, "stamp", default);
        (await store.ListAsync(user.Id, default)).Single().LastUsedAt.ShouldBe(clock.Now);
        clock.Now = session.ExpiresAt;
        (await store.ValidateAsync(user.Id, session.Id, "stamp", default)).ShouldBeNull();
        (await store.ListAsync(user.Id, default)).Single().Status.ShouldBe("Expired");
    }

    [Fact]
    public async Task Revocation_IsOwnedAndDurable_AndSecurityChangesInvalidateSessions()
    {
        await using var database = PostgreSqlTestDatabase.Create();
        await using var db = database.CreateContext();
        var user = new RomdUser { UserName = "session", SecurityStamp = "stamp" };
        db.Users.Add(user); await db.SaveChangesAsync();
        var store = new AccountSessionStore(db, new Clock());
        var first = await store.StartAsync(user.Id, "client", "Device", "stamp", default);
        var other = await store.StartAsync(user.Id, "client", "Other device", "stamp", default);
        (await store.RevokeAsync(Guid.NewGuid(), first.Id, null, user.Id, "Test", default)).ShouldBeFalse();
        await store.RevokeAsync(user.Id, null, first.Id, user.Id, "Other sessions revoked", default);
        (await store.ValidateAsync(user.Id, first.Id, "stamp", default)).ShouldNotBeNull();
        (await store.ValidateAsync(user.Id, other.Id, "stamp", default)).ShouldBeNull();
        var ended = (await store.ListAsync(user.Id, default)).Single(item => item.Id == other.Id);
        ended.RevokedBy.ShouldBe(user.Id); ended.RevokedAt.ShouldNotBeNull();
        await db.Users.Where(item => item.Id == user.Id).ExecuteUpdateAsync(set => set.SetProperty(item => item.SecurityStamp, "changed"));
        (await store.ValidateAsync(user.Id, first.Id, "stamp", default)).ShouldBeNull();
        (await store.ListAsync(user.Id, default)).Single(item => item.Id == first.Id).Status.ShouldBe("Invalidated");
    }
}
