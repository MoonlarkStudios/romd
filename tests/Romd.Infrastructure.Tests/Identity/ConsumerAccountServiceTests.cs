using Romd.Infrastructure.Tests.Helpers;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using ErrorOr;
using Romd.Consumer.Application.Account;
using Romd.Consumer.Application.Common.Persistence;
using Romd.Consumer.Application.Account.Commands.ChangeConsumerPassword;
using NSubstitute;
using Romd.Application.Common.Security;
using Romd.Infrastructure.Identity;
using Romd.Persistence;
using Romd.Persistence.Identity;
using Romd.PostgreSql.TestSupport;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Identity;

public sealed partial class ConsumerAccountServiceTests
{
    [Theory]
    [InlineData("password")]
    [InlineData("security")]
    [InlineData("concurrency")]
    public async Task ChangePasswordAsync_AccountChangedAfterVerification_PreservesNewerCredentials(string changed)
    {
        await using var database = PostgreSqlTestDatabase.Create();
        await using var db = database.CreateContext();
        var user = await SeedUserAsync(db);
        await using var competing = database.CreateContext();
        var hasher = CreateHasher();
        hasher.VerifyHashedPassword(Arg.Any<RomdUser>(), "original-hash", "current-password").Returns(_ =>
        {
            // A password reset commits after this request loaded the account but before its update.
            competing.Users.Where(candidate => candidate.Id == user.Id).ExecuteUpdate(set => set
                .SetProperty(candidate => candidate.PasswordHash, changed == "password" ? "reset-hash" : user.PasswordHash)
                .SetProperty(candidate => candidate.SecurityStamp, changed == "security" ? "reset-stamp" : user.SecurityStamp)
                .SetProperty(candidate => candidate.ConcurrencyStamp, changed == "concurrency" ? "reset-concurrency" : user.ConcurrencyStamp));
            return PasswordVerificationResult.Success;
        });
        var sessions = Substitute.For<IAccountSessions>();

        var result = await ChangePasswordAsync(db, CreateService(db, hasher, sessions),
            user.Id, "current-password", "new-password");

        result.IsError.ShouldBeTrue();
        result.FirstError.Code.ShouldBe("Consumer.PasswordChangeFailed");
        var persisted = await db.Users.AsNoTracking().SingleAsync();
        persisted.PasswordHash.ShouldBe(changed == "password" ? "reset-hash" : "original-hash");
        persisted.SecurityStamp.ShouldBe(changed == "security" ? "reset-stamp" : "original-stamp");
        persisted.ConcurrencyStamp.ShouldBe(changed == "concurrency" ? "reset-concurrency" : "original-concurrency");
        await sessions.DidNotReceiveWithAnyArgs().RevokeAsync(default, null, null, default, "", default);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ChangePasswordAsync_AccountDisabledAfterVerification_PreservesCredentialsAndSessions(bool suspended)
    {
        await using var database = PostgreSqlTestDatabase.Create();
        await using var db = database.CreateContext();
        var user = await SeedUserAsync(db);
        var sessions = new AccountSessionStore(db, TimeProvider.System);
        var session = await sessions.StartCommittedAsync(db, TimeProvider.System, user.Id, "client", "Device", user.SecurityStamp!, default);
        await using var competing = database.CreateContext();
        var hasher = CreateHasher();
        hasher.VerifyHashedPassword(Arg.Any<RomdUser>(), "original-hash", "current-password").Returns(_ =>
        {
            // Leave credentials and stamps unchanged to exercise account state independently.
            competing.Users.Where(candidate => candidate.Id == user.Id).ExecuteUpdate(set => set
                .SetProperty(candidate => candidate.IsSuspended, suspended)
                .SetProperty(candidate => candidate.RequiresActivation, !suspended));
            return PasswordVerificationResult.Success;
        });

        var result = await ChangePasswordAsync(db, CreateService(db, hasher, sessions),
            user.Id, "current-password", "new-password");

        result.IsError.ShouldBeTrue();
        result.FirstError.Code.ShouldBe("Consumer.PasswordChangeFailed");
        await using var verification = database.CreateContext();
        var persisted = await verification.Users.SingleAsync();
        persisted.PasswordHash.ShouldBe(user.PasswordHash);
        persisted.SecurityStamp.ShouldBe(user.SecurityStamp);
        persisted.ConcurrencyStamp.ShouldBe(user.ConcurrencyStamp);
        persisted.IsSuspended.ShouldBe(suspended);
        persisted.RequiresActivation.ShouldBe(!suspended);
        (await verification.AccountSessions.SingleAsync(item => item.Id == session.Id)).RevokedAt.ShouldBeNull();
    }

    [Fact]
    public async Task ChangePasswordAsync_RevocationFails_RollsBackCredentialsAndSessions()
    {
        await using var database = PostgreSqlTestDatabase.Create();
        await using var db = database.CreateContext();
        var user = await SeedUserAsync(db);
        var actualSessions = new AccountSessionStore(db, TimeProvider.System);
        var session = await actualSessions.StartCommittedAsync(db, TimeProvider.System, user.Id, "client", "Device", user.SecurityStamp!, default);
        var sessions = Substitute.For<IAccountSessions>();
        sessions.RevokeAsync(user.Id, null, null, user.Id, "Password changed", Arg.Any<CancellationToken>())
            .Returns(call => RevokeThenFailAsync(call.Arg<CancellationToken>()));

        async Task<bool> RevokeThenFailAsync(CancellationToken ct)
        {
            await actualSessions.RevokeAsync(user.Id, null, null, user.Id, "Password changed", ct);
            throw new IOException("Injected revocation failure");
        }

        await Should.ThrowAsync<IOException>(() => ChangePasswordAsync(db, CreateService(db, CreateHasher(), sessions), user.Id, "current-password", "new-password"));

        await using var verification = database.CreateContext();
        var persisted = await verification.Users.SingleAsync();
        persisted.PasswordHash.ShouldBe("original-hash");
        persisted.SecurityStamp.ShouldBe("original-stamp");
        persisted.ConcurrencyStamp.ShouldBe("original-concurrency");
        (await verification.AccountSessions.SingleAsync(item => item.Id == session.Id)).RevokedAt.ShouldBeNull();
    }

    [Fact]
    public async Task ChangePasswordAsync_ValidCurrentPassword_CommitsCredentialsAndRevocation()
    {
        await using var database = PostgreSqlTestDatabase.Create();
        await using var db = database.CreateContext();
        var user = await SeedUserAsync(db);
        var sessions = new AccountSessionStore(db, TimeProvider.System);
        var session = await sessions.StartCommittedAsync(db, TimeProvider.System, user.Id, "client", "Device", user.SecurityStamp!, default);

        var result = await ChangePasswordAsync(db, CreateService(db, CreateHasher(), sessions), user.Id, "current-password", "new-password");

        result.IsError.ShouldBeFalse();
        await using var verification = database.CreateContext();
        var persisted = await verification.Users.SingleAsync();
        persisted.PasswordHash.ShouldBe("new-hash");
        persisted.SecurityStamp.ShouldNotBe(user.SecurityStamp);
        persisted.ConcurrencyStamp.ShouldNotBe(user.ConcurrencyStamp);
        (await verification.AccountSessions.SingleAsync(item => item.Id == session.Id)).RevokedAt.ShouldNotBeNull();
    }

    private static Task<ErrorOr<Updated>> ChangePasswordAsync(RomdDbContext db, IConsumerPasswordChanger service,
        Guid userId, string currentPassword, string newPassword, CancellationToken ct = default, IConsumerSecurityTransaction? transaction = null)
    {
        var current = Substitute.For<ICurrentUser>();
        current.IsAuthenticated.Returns(true);
        current.UserId.Returns(userId);
        transaction ??= new ConsumerSecurityTransaction(new EfUnitOfWork(db), NullLogger<ConsumerSecurityTransaction>.Instance);
        return new ChangeConsumerPasswordCommandHandler(current, service, transaction)
            .HandleAsync(new(currentPassword, newPassword), ct);
    }

    private static async Task<RomdUser> SeedUserAsync(RomdDbContext db)
    {
        var user = new RomdUser
        {
            UserName = "consumer-account",
            PasswordHash = "original-hash",
            SecurityStamp = "original-stamp",
            ConcurrencyStamp = "original-concurrency"
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user;
    }

    private static IPasswordHasher<RomdUser> CreateHasher()
    {
        var hasher = Substitute.For<IPasswordHasher<RomdUser>>();
        hasher.VerifyHashedPassword(Arg.Any<RomdUser>(), "original-hash", "current-password")
            .Returns(PasswordVerificationResult.Success);
        hasher.HashPassword(Arg.Any<RomdUser>(), "new-password").Returns("new-hash");
        return hasher;
    }

    private static ConsumerAccountService CreateService(RomdDbContext db,
        IPasswordHasher<RomdUser>? hasher = null, IAccountSessions? sessions = null)
    {
        return new ConsumerAccountService(db, hasher ?? CreateHasher(), new AcceptPasswordPolicy(),
            sessions ?? new AccountSessionStore(db, TimeProvider.System));
    }

    private sealed class AcceptPasswordPolicy : IConsumerPasswordPolicy
    {
        public IdentityResult Validate(RomdUser user, string password) => IdentityResult.Success;
    }
}
