using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Romd.Admin.Application.Common.Persistence;
using Romd.Admin.Application.Common.Realtime;
using Romd.Application.Common.Ids;
using Romd.Contracts.Management.Libraries;
using Romd.Contracts.Management.Models;
using Romd.Contracts.Management.Realtime;
using Romd.Contracts.Management.Users;
using Romd.Domain.Identity;
using Romd.Domain.Jobs;
using Romd.Hosting.IntegrationTests.Infrastructure;
using Romd.Infrastructure.Realtime;
using Romd.Persistence;
using Romd.Persistence.Repositories;
using Shouldly;
using Xunit;

namespace Romd.Hosting.IntegrationTests.Endpoints;

[Collection(IntegrationTestCollection.Name)]
public sealed class UserLibraryTransactionSettlementTests
{
    [Theory]
    [InlineData("user", false)]
    [InlineData("user", true)]
    [InlineData("library", false)]
    [InlineData("library", true)]
    public async Task Create_AcknowledgedCommitThenDisposalFails_ReturnsCreatedWithDurableEffects(
        string surface, bool cleanupCancellation)
    {
        await using var host = new SettlementFixture();
        await host.InitializeAsync();
        using var client = host.CreateAuthenticatedClient();
        var before = await SnapshotAsync(host);
        string name = Name(surface);
        host.Probe.Arm(null, false, CleanupFault(cleanupCancellation));

        using var response = await CreateAsync(client, surface, name);

        response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        string persistedId = (await AssertEffectsAsync(host, surface, name, before, durable: true)).ShouldNotBeNull();
        if (surface == "user")
        {
            var body = (await response.Content.ReadFromJsonAsync<UserDto>(host.Json)).ShouldNotBeNull();
            body.Id.ToString().ShouldBe(persistedId);
            body.Email.ShouldBe(name);
            body.Roles.ShouldBe(["User"]);
        }
        else
        {
            var body = (await response.Content.ReadFromJsonAsync<LibraryDto>(host.Json)).ShouldNotBeNull();
            body.Id.ShouldBe(persistedId);
            body.Name.ShouldBe(name);
            body.NeedsMaterialization.ShouldBeTrue();
        }
        string collection = surface == "user" ? "users" : "libraries";
        response.Headers.Location.ShouldNotBeNull().OriginalString.ShouldBe($"/api/{collection}/{persistedId}");
        AssertSettlement(host.Probe, committed: true);
    }

    [Theory]
    [InlineData("user", false)]
    [InlineData("user", true)]
    [InlineData("library", false)]
    [InlineData("library", true)]
    public async Task Create_CommitFailsBeforeOrAfterDatabaseCommit_PreservesFailureEnvelopeAndActualDurability(
        string surface, bool lostAcknowledgement)
    {
        await using var host = new SettlementFixture();
        await host.InitializeAsync();
        using var client = host.CreateAuthenticatedClient();
        var before = await SnapshotAsync(host);
        string name = Name(surface);
        host.Probe.Arm(new IOException("private-commit-failure"), lostAcknowledgement, CleanupFault(cancellation: true));

        using var response = await CreateAsync(client, surface, name);

        string code = surface == "user" ? "Users.PersistenceFailed" : "Libraries.PersistenceFailed";
        using var problem = await ProblemEnvelope.ReadAsync(response, 500, code);
        problem.RootElement.GetProperty("detail").GetString()
            .ShouldBe(surface == "user" ? "User operation failed." : "Library operation failed.");
        string raw = await response.Content.ReadAsStringAsync();
        raw.ShouldNotContain("private-commit-failure");
        raw.ShouldNotContain("private-disposal-failure");
        raw.ShouldNotContain(nameof(IOException));
        raw.ShouldNotContain(nameof(OperationCanceledException));
        response.Headers.Location.ShouldBeNull();
        response.Headers.RetryAfter.ShouldBeNull();
        await AssertEffectsAsync(host, surface, name, before, durable: lostAcknowledgement);
        // An uncertain acknowledgement remains a failure response even when a fresh observer
        // proves this injected commit durable. Neither the handler nor the fixture retries it.
        AssertSettlement(host.Probe, committed: lostAcknowledgement);
        host.Probe.FlushesBeforeFailure.ShouldBe(lostAcknowledgement ? 0 : 1);
    }

    [Theory]
    [InlineData("user", false)]
    [InlineData("user", true)]
    [InlineData("library", false)]
    [InlineData("library", true)]
    public async Task Create_AnonymousOrNonAdmin_IsRejectedBeforeTransaction(string surface, bool authenticated)
    {
        await using var host = new SettlementFixture();
        await host.InitializeAsync();
        using var client = host.CreateClient();
        if (authenticated)
            client.WithTestUser(Guid.NewGuid(), "member@example.test", [RomdRoleType.User]);
        var before = await SnapshotAsync(host);
        host.Probe.Arm(new IOException("must-not-commit"), false, CleanupFault(cancellation: true));

        using var response = await CreateAsync(client, surface, Name(surface));

        response.StatusCode.ShouldBe(authenticated ? HttpStatusCode.Forbidden : HttpStatusCode.Unauthorized);
        host.Probe.Begins.ShouldBe(0);
        host.Probe.Commits.ShouldBe(0);
        host.Probe.Disposals.ShouldBe(0);
        (await SnapshotAsync(host)).ShouldBe(before);
    }

    [Theory]
    [InlineData("user", "Users.MissingRequiredFields")]
    [InlineData("library", "Libraries.NameRequired")]
    public async Task Create_RequiredFieldValidation_IsRejectedBeforeTransaction(string surface, string code)
    {
        await using var host = new SettlementFixture();
        await host.InitializeAsync();
        using var client = host.CreateAuthenticatedClient();
        var before = await SnapshotAsync(host);
        host.Probe.Arm(new IOException("must-not-commit"), false, CleanupFault(cancellation: true));

        using var response = await CreateAsync(client, surface, "");

        using var problem = await ProblemEnvelope.ReadAsync(response, 400, code);
        problem.RootElement.GetProperty("errors").EnumerateObject().Select(property => property.Name)
            .ShouldBe([surface == "user" ? "email" : "name"]);
        host.Probe.Begins.ShouldBe(0);
        host.Probe.Commits.ShouldBe(0);
        host.Probe.Disposals.ShouldBe(0);
        (await SnapshotAsync(host)).ShouldBe(before);
    }

    [Fact]
    public async Task AssignDefaultLibrary_NoEligibleChangeAndAcknowledgedDisposalFailure_ReturnsZeroWithoutChangingAccount()
    {
        await using var host = new SettlementFixture();
        await host.InitializeAsync();
        using var client = host.CreateAuthenticatedClient();
        using var libraryResponse = await client.PostAsJsonAsync("/api/libraries", new CreateLibraryRequest(
            Name("library"), IsDefault: true));
        libraryResponse.StatusCode.ShouldBe(HttpStatusCode.Created);
        var library = (await libraryResponse.Content.ReadFromJsonAsync<LibraryDto>(host.Json)).ShouldNotBeNull();
        using var userResponse = await client.PostAsJsonAsync("/api/users", new CreateUserRequest(
            Name("user"), "Password123", "User", library.Id));
        userResponse.StatusCode.ShouldBe(HttpStatusCode.Created);
        var user = (await userResponse.Content.ReadFromJsonAsync<UserDto>(host.Json)).ShouldNotBeNull();
        var before = await SnapshotAsync(host);
        host.Probe.Arm(null, false, CleanupFault(cancellation: true));

        using var response = await client.PostAsJsonAsync("/api/users/assign-default-library", new { userIds = new[] { user.Id } });

        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        body.RootElement.GetProperty("updatedCount").GetInt32().ShouldBe(0);
        (await SnapshotAsync(host)).ShouldBe(before);
        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<RomdDbContext>();
        var stored = await db.Users.SingleAsync(row => row.Id == user.Id);
        IdCoder.TryDecode(library.Id, out int libraryId).ShouldBeTrue();
        stored.LibraryId.ShouldBe(libraryId);
        stored.UpdatedAt.ShouldBe(user.UpdatedAt);
        AssertSettlement(host.Probe, committed: true);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Materialize_ExistingPendingOrClaimedJobAndDisposalFailure_ConvergesWithoutCommitOrFlush(bool claimed)
    {
        await using var host = new SettlementFixture();
        await host.InitializeAsync();
        using var client = host.CreateAuthenticatedClient();
        using var libraryResponse = await client.PostAsJsonAsync("/api/libraries", new CreateLibraryRequest(Name("library")));
        libraryResponse.StatusCode.ShouldBe(HttpStatusCode.Created);
        var library = (await libraryResponse.Content.ReadFromJsonAsync<LibraryDto>(host.Json)).ShouldNotBeNull();
        string route = $"/api/libraries/{library.Id}/materialize";
        using var first = await client.PostAsync(route, null);
        first.StatusCode.ShouldBe(HttpStatusCode.Accepted, await first.Content.ReadAsStringAsync());
        var accepted = (await first.Content.ReadFromJsonAsync<JobReference>()).ShouldNotBeNull();
        if (claimed)
        {
            // This scope starts after mapped acceptance has committed and disposed its context.
            // The real shared claim owns the transition; the fixture never edits its phase.
            await using var claimScope = host.Services.CreateAsyncScope();
            var db = claimScope.ServiceProvider.GetRequiredService<RomdDbContext>();
            var claims = new ClaimedJobRepository<MaterializationJob, MaterializationJobRepository>(
                new(db, TimeProvider.System), db, TimeProvider.System, new());
            (await claims.TryClaimExecutionAsync(accepted.JobId, "settlement-existing-job")).ShouldNotBeNull();
        }
        var before = await SnapshotAsync(host);
        await using var observer = host.Services.CreateAsyncScope();
        var read = observer.ServiceProvider.GetRequiredService<RomdDbContext>();
        var original = await read.Jobs.AsNoTracking().SingleAsync(row => row.Id == accepted.JobId);
        original.Phase.ShouldBe(claimed ? "Materializing" : "Pending");
        host.Probe.Arm(new IOException("must-not-commit"), false, CleanupFault(cancellation: false));

        using var response = await client.PostAsync(route, null);

        response.StatusCode.ShouldBe(HttpStatusCode.Accepted, await response.Content.ReadAsStringAsync());
        (await response.Content.ReadFromJsonAsync<JobReference>()).ShouldNotBeNull().JobId.ShouldBe(accepted.JobId);
        response.Headers.Location.ShouldNotBeNull().OriginalString.ShouldBe($"/api/jobs/{accepted.JobId}");
        response.Headers.ETag.ShouldBeNull();
        host.Probe.Begins.ShouldBe(1);
        host.Probe.Commits.ShouldBe(0);
        host.Probe.DatabaseCommits.ShouldBe(0);
        host.Probe.Flushes.ShouldBe(0);
        host.Probe.Disposals.ShouldBe(1);
        (await SnapshotAsync(host)).ShouldBe(before);
        var stored = await read.Jobs.AsNoTracking().SingleAsync(row => row.Id == accepted.JobId);
        stored.Phase.ShouldBe(original.Phase);
        stored.ExecutionFenceToken.ShouldBe(original.ExecutionFenceToken);
        stored.ExecutionLeaseExpiresAtUtc.ShouldBe(original.ExecutionLeaseExpiresAtUtc);
    }

    private static string Name(string surface) => surface == "user"
        ? $"settlement-{Guid.NewGuid():N}@example.test"
        : $"Settlement {Guid.NewGuid():N}";

    private static Exception CleanupFault(bool cancellation) => cancellation
        ? new OperationCanceledException("private-disposal-failure", new CancellationToken(canceled: true))
        : new IOException("private-disposal-failure");

    private static Task<HttpResponseMessage> CreateAsync(HttpClient client, string surface, string name) => surface == "user"
        ? client.PostAsJsonAsync("/api/users", new CreateUserRequest(name, "Password123", "User"))
        : client.PostAsJsonAsync("/api/libraries", new CreateLibraryRequest(name));

    private sealed record Snapshot(int Users, int UserRoles, int Libraries, int Events, int Jobs, int Dispatches);

    private static async Task<Snapshot> SnapshotAsync(SettlementFixture host)
    {
        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<RomdDbContext>();
        return new(await db.Users.CountAsync(), await db.UserRoles.CountAsync(), await db.Libraries.CountAsync(),
            await db.AdminRealtimeOutboxEvents.CountAsync(), await db.Jobs.CountAsync(), await db.JobDispatches.CountAsync());
    }

    private static async Task<string?> AssertEffectsAsync(SettlementFixture host, string surface, string name,
        Snapshot before, bool durable)
    {
        int inserted = durable ? 1 : 0;
        (await SnapshotAsync(host)).ShouldBe(surface == "user"
            ? before with { Users = before.Users + inserted, UserRoles = before.UserRoles + inserted }
            : before with { Libraries = before.Libraries + inserted, Events = before.Events + inserted });
        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<RomdDbContext>();
        if (surface == "user")
        {
            var row = await db.Users.SingleOrDefaultAsync(user => user.Email == name);
            if (!durable)
            {
                row.ShouldBeNull();
                return null;
            }
            var stored = row.ShouldNotBeNull();
            var roles = await (from assignment in db.UserRoles
                               join role in db.Roles on assignment.RoleId equals role.Id
                               where assignment.UserId == stored.Id
                               select role.Name).ToArrayAsync();
            roles.ShouldBe(["User"]);
            return stored.Id.ToString();
        }

        var library = await db.Libraries.SingleOrDefaultAsync(row => row.Name == name);
        var events = (await db.AdminRealtimeOutboxEvents
                .Where(row => row.EventType == AdminRealtimeEventTypes.LibraryUpdated)
                .Select(row => row.PayloadJson).ToListAsync())
            .Select(AdminRealtimePayloadSerializer.Deserialize<AdminRealtimeLibraryUpdatedPayload>)
            .Where(payload => payload.Name == name).ToArray();
        if (!durable)
        {
            library.ShouldBeNull();
            events.ShouldBeEmpty();
            return null;
        }
        var committed = library.ShouldNotBeNull();
        var payload = events.ShouldHaveSingleItem();
        payload.LibraryId.ShouldBe(IdCoder.Encode(committed.Id));
        payload.NeedsMaterialization.ShouldBeTrue();
        return payload.LibraryId;
    }

    private static void AssertSettlement(SettlementProbe probe, bool committed)
    {
        probe.Begins.ShouldBe(1);
        probe.Commits.ShouldBe(1);
        probe.DatabaseCommits.ShouldBe(committed ? 1 : 0);
        probe.Disposals.ShouldBe(1);
    }

    private sealed class SettlementFixture : IntegrationTestFixture
    {
        public SettlementProbe Probe { get; } = new();
        public JsonSerializerOptions Json => Services.GetRequiredService<IOptions<JsonOptions>>().Value.SerializerOptions;

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureTestServices(services =>
            {
                // No worker or relay may consume the committed outbox while a fresh request
                // scope verifies its atomic relationship with the library mutation.
                services.RemoveAll<IHostedService>();
                services.RemoveAll<IUnitOfWork>();
                services.AddScoped<IUnitOfWork>(provider => new SettlementWork(
                    new EfUnitOfWork(provider.GetRequiredService<RomdDbContext>()), Probe));
            });
        }
    }

    private sealed class SettlementProbe
    {
        public FaultPlan? Plan { get; private set; }
        public int Begins { get; set; }
        public int Commits { get; set; }
        public int DatabaseCommits { get; set; }
        public int Disposals { get; set; }
        public int FlushesBeforeFailure { get; set; }
        public int Flushes { get; set; }

        public void Arm(Exception? commitFailure, bool afterCommit, Exception cleanupFailure)
        {
            Plan = new(commitFailure, afterCommit, cleanupFailure);
            Begins = Commits = DatabaseCommits = Disposals = FlushesBeforeFailure = Flushes = 0;
        }
    }

    private sealed record FaultPlan(Exception? CommitFailure, bool AfterCommit, Exception CleanupFailure);

    private sealed class SettlementWork(EfUnitOfWork inner, SettlementProbe probe) : IUnitOfWork
    {
        public Task FlushAsync(CancellationToken ct = default)
        {
            if (probe.Plan is not null)
                probe.Flushes++;
            return inner.FlushAsync(ct);
        }
        public TransactionConflict ConflictOf(Exception error) => inner.ConflictOf(error);

        public async Task<ITransaction> BeginTransactionAsync(CancellationToken ct = default)
        {
            var transaction = await inner.BeginTransactionAsync(ct);
            if (probe.Plan is not { } plan)
                return transaction;
            probe.Begins++;
            return new SettlementTransaction(transaction, inner, probe, plan);
        }
    }

    private sealed class SettlementTransaction(ITransaction inner, EfUnitOfWork work, SettlementProbe probe, FaultPlan plan) : ITransaction
    {
        public async Task CommitAsync(CancellationToken ct = default)
        {
            probe.Commits++;
            if (plan.CommitFailure is { } before && !plan.AfterCommit)
            {
                // Exercise rollback of actual database writes, including a flushed outbox,
                // rather than failing before EF has sent the pending mutation to PostgreSQL.
                await work.FlushAsync(ct);
                probe.FlushesBeforeFailure++;
                throw before;
            }
            await inner.CommitAsync(ct);
            probe.DatabaseCommits++;
            if (plan.CommitFailure is { } after)
                throw after;
        }

        public Task RollbackAsync(CancellationToken ct = default) => inner.RollbackAsync(ct);

        public async ValueTask DisposeAsync()
        {
            await inner.DisposeAsync();
            probe.Disposals++;
            throw plan.CleanupFailure;
        }
    }
}
