using ErrorOr;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Romd.Admin.Application.Common.Persistence;
using Romd.Admin.Application.Libraries;
using Romd.Admin.Application.Libraries.Commands.CreateLibrary;
using Romd.Admin.Application.Libraries.Commands.DeleteLibrary;
using Romd.Admin.Application.Libraries.Commands.ForceMaterializeLibrary;
using Romd.Admin.Application.Libraries.Commands.UpdateLibrary;
using Romd.Admin.Application.Users;
using Romd.Admin.Application.Users.Commands.AssignDefaultLibraryToUsers;
using Romd.Admin.Application.Users.Commands.AssignUserLibrary;
using Romd.Admin.Application.Users.Commands.AssignUserRole;
using Romd.Admin.Application.Users.Commands.CreateUser;
using Romd.Admin.Application.Users.Commands.DeleteUser;
using Romd.Admin.Application.Users.Commands.UpdateUser;
using Romd.Application.Common.Ids;
using Romd.Application.Common.Security;
using Romd.Domain.Identity;
using Romd.Domain.Jobs;
using Romd.Infrastructure.Identity;
using Romd.Infrastructure.Realtime;
using Romd.Infrastructure.Storage;
using Romd.Infrastructure.Tests.Helpers;
using Romd.Persistence;
using Romd.Persistence.Entities;
using Romd.Persistence.Identity;
using Romd.Persistence.ReferenceData;
using Romd.Persistence.Repositories;
using Romd.PostgreSql.TestSupport;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Identity;

public sealed class UserLibraryTransactionSettlementTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UnixEpoch.AddDays(1000);
    public static IEnumerable<object[]> SettlementCases =>
        from owner in new[] { "create-user", "update-user", "delete-user", "role", "assign", "default", "create-library", "update-library", "delete-library", "force" }
        from outcome in new[] { "ack", "before-io", "before-oce", "after-io", "after-oce" }
        select new object[] { owner, outcome };

    [Theory]
    [MemberData(nameof(SettlementCases))]
    public async Task Owner_RealProviderSettlementAndCleanupFaultKeepAtomicStateAndOriginalOutcome(string owner, string outcome)
    {
        await using var database = PostgreSqlTestDatabase.Create();
        await using var provider = CreateServices(database);
        var seeded = await SeedAsync(database);
        await using var scope = provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<RomdDbContext>();
        using var cancellation = new CancellationTokenSource();
        bool cancelled = outcome.EndsWith("oce", StringComparison.Ordinal);
        Exception primary = cancelled ? new OperationCanceledException("Original commit interruption", cancellation.Token)
            : new IOException("Original provider acknowledgement loss");
        string boundary = outcome.StartsWith("before", StringComparison.Ordinal) ? "before"
            : outcome.StartsWith("after", StringComparison.Ordinal) ? "after" : "none";
        var faults = new ProviderTransactionFaults(boundary, primary, cancelled ? cancellation.Cancel : null);
        faults.Register(db);
        var enqueuer = Substitute.For<IMaterializationJobEnqueuer>();
        Exception? observed = null;
        (bool Error, string? Code) result = default;

        try
        { result = await ExecuteAsync(owner, seeded, scope.ServiceProvider, enqueuer, cancellation.Token); }
        catch (Exception error) { observed = error; }

        if (cancelled)
        {
            observed.ShouldBeSameAs(primary);
            ((OperationCanceledException)observed!).CancellationToken.ShouldBe(cancellation.Token);
        }
        else
        {
            observed.ShouldBeNull();
            // Force retains its existing post-failure active-job convergence read, without replaying acceptance.
            bool converged = owner == "force" && boundary == "after";
            result.Error.ShouldBe(boundary != "none" && !converged);
            if (result.Error)
                result.Code.ShouldBe(owner.EndsWith("library", StringComparison.Ordinal) || owner == "force"
                    ? "Libraries.PersistenceFailed" : "Users.PersistenceFailed");
        }
        faults.Commits.ShouldBe(boundary == "before" ? 0 : 1);
        faults.Disposals.ShouldBe(1);
        db.Database.CurrentTransaction.ShouldBeNull();
        await db.SaveChangesAsync(); // Failed settlement must not leave staged work to resurrect later.
        await using var read = database.CreateContext();
        bool durable = boundary != "before";
        await AssertStoredStateAsync(read, owner, seeded, durable);
        await enqueuer.Received(owner == "force" && outcome == "ack" ? 1 : 0)
            .EnqueueAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Force_ExistingPendingJobAndSoleDisposalFaultConvergesWithoutCommitOrFlushingUnrelatedWork()
    {
        await using var database = PostgreSqlTestDatabase.Create();
        await using var provider = CreateServices(database);
        var seeded = await SeedAsync(database);
        Guid jobId;
        await using (var seed = database.CreateContext())
        {
            IUnitOfWork work = new EfUnitOfWork(seed);
            await using var transaction = await work.BeginTransactionAsync();
            var job = MaterializationJob.Create(seeded.TargetId, "Target", Now, seeded.UserId);
            await new MaterializationJobRepository(seed, TimeProvider.System).AddStagedAsync(job);
            await transaction.CommitAsync();
            jobId = job.Id;
        }
        await using var scope = provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<RomdDbContext>();
        db.Libraries.Add(new LibraryEntity { Name = "Unrelated pending state", ConfigurationJson = "{}", ConfigurationState = "Valid" });
        var faults = new ProviderTransactionFaults();
        faults.Register(db);
        var enqueuer = Substitute.For<IMaterializationJobEnqueuer>();

        var result = await ExecuteAsync("force", seeded, scope.ServiceProvider, enqueuer, default);

        result.Error.ShouldBeFalse();
        faults.Commits.ShouldBe(0);
        faults.Disposals.ShouldBe(1);
        await db.SaveChangesAsync();
        await using var read = database.CreateContext();
        (await read.Jobs.SingleAsync()).Id.ShouldBe(jobId);
        (await read.Libraries.AnyAsync(row => row.Name == "Unrelated pending state")).ShouldBeFalse();
        await enqueuer.DidNotReceiveWithAnyArgs().EnqueueAsync(default, default);
    }

    private static ServiceProvider CreateServices(PostgreSqlTestDatabase database)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDataProtection();
        services.AddSingleton<TimeProvider>(new FixedClock());
        services.AddSingleton<IAuditContext, AuditActor>();
        services.AddDbContext<RomdDbContext>(options => options.UseNpgsql(database.ConnectionString).UseOpenIddict()
            .ReplaceService<IRelationalTransactionFactory, ProviderTransactionFaults.Factory>());
        services.AddIdentityCore<RomdUser>(RomdIdentityOptions.Configure).AddRoles<RomdIdentityRole>()
            .AddEntityFrameworkStores<RomdDbContext>().AddDefaultTokenProviders();
        services.AddOpenIddict().AddCore(options => options.UseEntityFrameworkCore().UseDbContext<RomdDbContext>());
        services.AddScoped<IAccountSessions, AccountSessionStore>();
        services.AddScoped<IUserAdministration, UserAdministration>();
        return services.BuildServiceProvider();
    }

    private sealed record Seed(Guid UserId, string Stamp, int DefaultId, int TargetId, Guid TargetRevision);
    private static async Task<Seed> SeedAsync(PostgreSqlTestDatabase database)
    {
        await using var db = database.CreateContext();
        var roles = Enum.GetNames<RomdRoleType>().Select(name => new RomdIdentityRole(name)).ToArray();
        var user = new RomdUser
        {
            Id = Guid.NewGuid(),
            Email = "existing@example.com",
            NormalizedEmail = "EXISTING@EXAMPLE.COM",
            UserName = "existing@example.com",
            NormalizedUserName = "EXISTING@EXAMPLE.COM",
            EmailConfirmed = true,
            PasswordHash = "Unused fixture password hash",
            SecurityStamp = Guid.NewGuid().ToString(),
            ConcurrencyStamp = Guid.NewGuid().ToString(),
            CreatedAt = Now.AddDays(-1)
        };
        var initial = new LibraryEntity { Name = "Default", ConfigurationJson = "{}", ConfigurationState = "Valid", IsDefault = true };
        var target = new LibraryEntity
        {
            Name = "Target",
            ConfigurationJson = "{}",
            ConfigurationState = "Valid",
            NeedsMaterialization = false,
            LastMaterializedAt = Now.AddDays(-1)
        };
        db.Roles.AddRange(roles);
        db.Users.Add(user);
        db.Libraries.AddRange(initial, target);
        db.UserRoles.Add(new IdentityUserRole<Guid> { UserId = user.Id, RoleId = roles.Single(role => role.Name == "User").Id });
        await db.SaveChangesAsync();
        return new(user.Id, user.ConcurrencyStamp!, initial.Id, target.Id, target.MaterializationRevision);
    }

    private static async Task<(bool Error, string? Code)> ExecuteAsync(string owner, Seed seed, IServiceProvider services,
        IMaterializationJobEnqueuer enqueuer, CancellationToken ct)
    {
        var db = services.GetRequiredService<RomdDbContext>();
        var users = services.GetRequiredService<IUserAdministration>();
        var libraries = new LibraryRepository(db);
        var jobs = new MaterializationJobRepository(db, TimeProvider.System);
        var work = new EfUnitOfWork(db);
        var outbox = new AdminRealtimeOutbox(db, TimeProvider.System);
        var current = Substitute.For<ICurrentUser>();
        current.UserId.Returns(seed.UserId);
        var catalog = new ReferenceCatalogService(db, TestReferenceBlobs.For(db));
        var clock = new FixedClock();
        return owner switch
        {
            "create-user" => Outcome(await new CreateUserCommandHandler(users, libraries, work, clock, NullLogger<CreateUserCommandHandler>.Instance).HandleAsync(new("created@example.com", "Password123", "Manager", null), ct)),
            "update-user" => Outcome(await new UpdateUserCommandHandler(users, work, clock, NullLogger<UpdateUserCommandHandler>.Instance).HandleAsync(new(seed.UserId, "changed@example.com", null), ct)),
            "delete-user" => Outcome(await new DeleteUserCommandHandler(users, Substitute.For<ICurrentUser>(), work, NullLogger<DeleteUserCommandHandler>.Instance).HandleAsync(new(seed.UserId), ct)),
            "role" => Outcome(await new AssignUserRoleCommandHandler(users, work, clock, NullLogger<AssignUserRoleCommandHandler>.Instance).HandleAsync(new(seed.UserId, "Manager"), ct)),
            "assign" => Outcome(await new AssignUserLibraryCommandHandler(users, libraries, work, clock, NullLogger<AssignUserLibraryCommandHandler>.Instance).HandleAsync(new(seed.UserId, IdCoder.Encode(seed.TargetId)), ct)),
            "default" => Outcome(await new AssignDefaultLibraryToUsersCommandHandler(users, libraries, work, clock, NullLogger<AssignDefaultLibraryToUsersCommandHandler>.Instance).HandleAsync(new([seed.UserId]), ct)),
            "create-library" => Outcome(await new CreateLibraryCommandHandler(catalog, libraries, outbox, work, NullLogger<CreateLibraryCommandHandler>.Instance, RomMediaPolicyComposition.CreateDeliveryPolicy(), clock).HandleAsync(new("Created", new(), true), ct)),
            "update-library" => Outcome(await new UpdateLibraryCommandHandler(catalog, libraries, outbox, work, NullLogger<UpdateLibraryCommandHandler>.Instance, RomMediaPolicyComposition.CreateDeliveryPolicy(), clock).HandleAsync(new(seed.TargetId, "Changed", new(), true), ct)),
            "delete-library" => Outcome(await new DeleteLibraryCommandHandler(libraries, outbox, work, NullLogger<DeleteLibraryCommandHandler>.Instance).HandleAsync(new(seed.TargetId), ct)),
            "force" => Outcome(await new ForceMaterializeLibraryCommandHandler(libraries, jobs, enqueuer, outbox, current, work, NullLogger<ForceMaterializeLibraryCommandHandler>.Instance, clock).HandleAsync(new(seed.TargetId), ct)),
            _ => throw new ArgumentOutOfRangeException(nameof(owner))
        };
    }

    private static async Task AssertStoredStateAsync(RomdDbContext db, string owner, Seed seed, bool durable)
    {
        var user = await db.Users.SingleOrDefaultAsync(row => row.Id == seed.UserId);
        (user is null).ShouldBe(owner == "delete-user" && durable);
        if (owner == "create-user")
        {
            var created = await db.Users.SingleOrDefaultAsync(row => row.Email == "created@example.com");
            (created is not null).ShouldBe(durable);
            if (created is not null)
            {
                created.LibraryId.ShouldBe(seed.DefaultId);
                (await RolesAsync(db, created.Id)).ShouldBe(["Manager"]);
            }
        }
        if (owner == "update-user")
            user!.Email.ShouldBe(durable ? "changed@example.com" : "existing@example.com");
        if (owner == "role")
            (await RolesAsync(db, seed.UserId)).ShouldBe(durable ? ["Manager"] : ["User"]);
        if (owner == "delete-user")
            (await db.UserRoles.AnyAsync(row => row.UserId == seed.UserId)).ShouldBe(!durable);
        if (owner is "assign" or "default")
            user!.LibraryId.ShouldBe(durable ? owner == "assign" ? seed.TargetId : seed.DefaultId : null);
        if (owner is "update-user" or "role" or "assign" or "default")
        {
            user!.UpdatedAt.ShouldBe(durable ? Now : null);
            if (durable)
                user.ConcurrencyStamp.ShouldNotBe(seed.Stamp);
            else
                user.ConcurrencyStamp.ShouldBe(seed.Stamp);
        }
        if (owner == "create-library")
        {
            var created = await db.Libraries.SingleOrDefaultAsync(row => row.Name == "Created");
            (created is not null).ShouldBe(durable);
            if (created is not null)
                created.IsDefault.ShouldBeTrue();
        }
        if (owner is "create-library" or "update-library")
            (await db.Libraries.SingleAsync(row => row.Id == seed.DefaultId)).IsDefault.ShouldBe(!durable);
        var target = await db.Libraries.SingleOrDefaultAsync(row => row.Id == seed.TargetId);
        (target is null).ShouldBe(owner == "delete-library" && durable);
        if (owner == "update-library")
        {
            target!.Name.ShouldBe(durable ? "Changed" : "Target");
            target.IsDefault.ShouldBe(durable);
        }
        if (owner == "force")
        {
            var job = await db.Jobs.OfType<MaterializationJobEntity>().SingleOrDefaultAsync();
            (job is not null).ShouldBe(durable);
            if (job is not null)
            {
                job.Phase.ShouldBe("Pending");
                job.CreatedByUserId.ShouldBe(seed.UserId);
                job.LibraryId.ShouldBe(seed.TargetId);
                job.HangfireJobId.ShouldBeNull();
                target!.MaterializationRevision.ShouldNotBe(seed.TargetRevision);
            }
            else
                target!.MaterializationRevision.ShouldBe(seed.TargetRevision);
        }
        (await db.AdminRealtimeOutboxEvents.CountAsync()).ShouldBe(durable && (owner.EndsWith("library", StringComparison.Ordinal) || owner == "force") ? 1 : 0);
    }

    private static Task<string?[]> RolesAsync(RomdDbContext db, Guid id) =>
        (from link in db.UserRoles join role in db.Roles on link.RoleId equals role.Id where link.UserId == id select role.Name).ToArrayAsync();
    private static (bool Error, string? Code) Outcome<T>(ErrorOr<T> result) => (result.IsError, result.IsError ? result.FirstError.Code : null);
    private sealed class FixedClock : TimeProvider { public override DateTimeOffset GetUtcNow() => Now; }
    private sealed class AuditActor : IAuditContext { public Guid ActorId => Guid.Empty; public bool IsSystem => true; }
}
