using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Npgsql;
using Romd.Admin.Application.Common.Persistence;
using Romd.Admin.Application.Source;
using Romd.Admin.Application.Source.Platform;
using Romd.Admin.Application.Source.Platform.Commands.AddPlatformAlias;
using Romd.Application.Common.Ids;
using Romd.Domain.ReferenceData;
using Romd.Domain.Source.Platform;
using Romd.Persistence;
using Romd.Persistence.Entities;
using Romd.Persistence.Repositories;
using Romd.PostgreSql.TestSupport;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Persistence;

public sealed class PlatformAliasTransactionTests : IDisposable
{
    private static readonly DateTimeOffset CreatedAt = new DateTimeOffset(2026, 11, 4, 12, 34, 56, TimeSpan.FromHours(5.5)).AddTicks(1234567);
    private readonly PostgreSqlTestDatabase _database = PostgreSqlTestDatabase.Create();

    public PlatformAliasTransactionTests()
    {
        using var db = _database.CreateContext();
        for (int id = 1; id <= 2; id++)
            db.Platforms.Add(new PlatformEntity
            {
                Id = id,
                Name = "Test " + id,
                ShortName = "test" + id,
                CanonicalKey = "test" + id,
                BaseName = "Test " + id,
                BaseCompactLabel = "Test " + id,
                Ownership = ReferenceOwnership.Romd,
                BuiltInVersion = 1
            });
        db.SaveChanges();
    }

    public void Dispose() => _database.Dispose();

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HandleAsync_NameOrProvider_PreservesGeneratedContractAndInstallationOwnership(bool provider)
    {
        await using var db = _database.CreateContext();
        var command = AddPlatformAliasCommand.Create(1, provider ? "provider" : "name",
            provider ? " 19 " : " Super  Famicom ", " IGDB ").Value;

        var result = await Handler(db).HandleAsync(command);

        result.IsError.ShouldBeFalse();
        var alias = result.Value;
        alias.Id.ShouldBeGreaterThan(0);
        alias.CreatedAt.ShouldBe(CreatedAt);
        var contract = alias.ToContract();
        contract.Id.ShouldBe(IdCoder.Encode(alias.Id));
        contract.Type.ShouldBe(provider ? "provider" : "name");
        contract.Provider.ShouldBe(provider ? "igdb" : null);
        contract.Value.ShouldBe(provider ? "19" : "Super  Famicom");
        await using var verification = _database.CreateContext();
        var row = await verification.PlatformAliases.SingleAsync();
        row.Id.ShouldBe(alias.Id);
        row.NormalizedValue.ShouldBe(provider ? "19" : "super famicom");
        row.CreatedAt.ShouldBe(DateTimeOffset.FromUnixTimeMilliseconds(CreatedAt.ToUnixTimeMilliseconds()));
        row.Ownership.ShouldBe(ReferenceOwnership.Installation);
    }

    [Fact]
    public async Task AddStagedAsync_TwoInsertions_ReceiptsOwnTheirIdentitiesAndRequireCallerFlush()
    {
        await using var db = _database.CreateContext();
        var work = new EfUnitOfWork(db);
        await using var transaction = await work.BeginTransactionAsync();
        var repository = new PlatformAliasRepository(db);
        var first = await repository.AddStagedAsync(PlatformAlias.CreateName(1, "First", CreatedAt));
        var second = await repository.AddStagedAsync(PlatformAlias.CreateName(2, "Second", CreatedAt));
        Should.Throw<InvalidOperationException>(() => first.ReadAfterFlush());
        Should.Throw<InvalidOperationException>(() => second.ReadAfterFlush());
        (await db.PlatformAliases.CountAsync()).ShouldBe(0);

        await work.FlushAsync();

        var firstAlias = first.ReadAfterFlush();
        var secondAlias = second.ReadAfterFlush();
        firstAlias.Value.ShouldBe("First");
        secondAlias.Value.ShouldBe("Second");
        firstAlias.Id.ShouldNotBe(secondAlias.Id);
        await using (var outside = _database.CreateContext())
            (await outside.PlatformAliases.CountAsync()).ShouldBe(0);
        await transaction.CommitAsync();
        await using var verification = _database.CreateContext();
        (await verification.PlatformAliases.SingleAsync(row => row.Id == firstAlias.Id)).PlatformId.ShouldBe(1);
        (await verification.PlatformAliases.SingleAsync(row => row.Id == secondAlias.Id)).PlatformId.ShouldBe(2);
    }

    [Fact]
    public async Task AddStagedAsync_MissingTransactionExistingIdentityOrCancellation_RejectsBeforeTracking()
    {
        await using var db = _database.CreateContext();
        var repository = new PlatformAliasRepository(db);
        var alias = PlatformAlias.CreateName(1, "New", CreatedAt);
        await Should.ThrowAsync<InvalidOperationException>(() => repository.AddStagedAsync(alias));
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        var cancelled = await Should.ThrowAsync<OperationCanceledException>(() => repository.AddStagedAsync(alias, cancellation.Token));
        cancelled.CancellationToken.ShouldBe(cancellation.Token);
        var work = new EfUnitOfWork(db);
        await using var transaction = await work.BeginTransactionAsync();

        var error = await Should.ThrowAsync<ArgumentException>(() => repository.AddStagedAsync(
            PlatformAlias.Rehydrate(42, 1, PlatformAliasType.Name, "Existing", "existing", null, CreatedAt)));

        error.ParamName.ShouldBe("alias");
        db.ChangeTracker.Entries().ShouldBeEmpty();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HandleAsync_ConcurrentDuplicateChecks_PreservesExistingDatabaseConstraintOutcome(bool provider)
    {
        var bothChecked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int arrivals = 0;
        async Task<(int? Id, string? Conflict)> AttemptAsync(int ordinal)
        {
            await using var db = _database.CreateContext();
            int platformId = provider ? 1 : ordinal;
            var real = new PlatformAliasRepository(db);
            var port = Substitute.For<IPlatformAliasRepository>();
            async Task<bool> CheckAsync(CancellationToken ct)
            {
                bool exists = provider
                    ? await real.ProviderMappingExistsAsync(platformId, "igdb", ct)
                    : await real.NameAliasExistsAsync("super famicom", ct);
                if (Interlocked.Increment(ref arrivals) == 2)
                    bothChecked.TrySetResult();
                await bothChecked.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);
                return exists;
            }
            port.NameAliasExistsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(call => CheckAsync(call.Arg<CancellationToken>()));
            port.ProviderMappingExistsAsync(Arg.Any<int>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(call => CheckAsync(call.Arg<CancellationToken>()));
            port.AddStagedAsync(Arg.Any<PlatformAlias>(), Arg.Any<CancellationToken>())
                .Returns(call => real.AddStagedAsync(call.Arg<PlatformAlias>(), call.Arg<CancellationToken>()));
            try
            {
                var result = await Handler(db, aliases: port).HandleAsync(AddPlatformAliasCommand.Create(
                    platformId, provider ? "provider" : "name", provider ? ordinal.ToString() : "Super Famicom", "igdb").Value);
                result.IsError.ShouldBeFalse();
                return (result.Value.Id, null);
            }
            catch (DbUpdateException error) when (error.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
            {
                return (null, PostgresErrorCodes.UniqueViolation);
            }
        }

        var results = await Task.WhenAll(AttemptAsync(1), AttemptAsync(2));

        results.Count(result => result.Id is > 0).ShouldBe(1);
        results.Count(result => result.Conflict == PostgresErrorCodes.UniqueViolation).ShouldBe(1);
        await using var verification = _database.CreateContext();
        (await verification.PlatformAliases.CountAsync()).ShouldBe(1);
    }

    [Theory]
    [InlineData(Failure.AfterIdentityFlush)]
    [InlineData(Failure.CancelAfterIdentityFlush)]
    [InlineData(Failure.BeforeCommit)]
    public async Task HandleAsync_FailureAfterGeneratedIdentity_RollsBackAliasAndDoesNotLeakStaging(Failure failure)
    {
        await using var db = _database.CreateContext();
        using var cancellation = new CancellationTokenSource();
        var work = new InterruptAliasCreation(new EfUnitOfWork(db), db, cancellation, failure);
        var operation = () => Handler(db, work).HandleAsync(AddPlatformAliasCommand.Create(1, "name", "New").Value, cancellation.Token);
        if (failure == Failure.CancelAfterIdentityFlush)
        {
            var error = await Should.ThrowAsync<OperationCanceledException>(operation);
            error.CancellationToken.ShouldBe(cancellation.Token);
        }
        else
            await Should.ThrowAsync<IOException>(operation);

        work.GeneratedId.ShouldBeGreaterThan(0);
        db.ChangeTracker.Entries().ShouldBeEmpty();
        await db.SaveChangesAsync();
        await using var verification = _database.CreateContext();
        (await verification.PlatformAliases.CountAsync()).ShouldBe(0);
        (await verification.Jobs.CountAsync()).ShouldBe(0);
        (await verification.JobDispatches.CountAsync()).ShouldBe(0);
        (await verification.AdminRealtimeOutboxEvents.CountAsync()).ShouldBe(0);
    }

    private static AddPlatformAliasCommandHandler Handler(RomdDbContext db, IUnitOfWork? work = null,
        IPlatformAliasRepository? aliases = null) => new(new PlatformRepository(db), aliases ?? new PlatformAliasRepository(db),
        work ?? new EfUnitOfWork(db), NullLogger<AddPlatformAliasCommandHandler>.Instance, new FixedClock());

    private sealed class FixedClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => CreatedAt;
    }

    public enum Failure { AfterIdentityFlush, CancelAfterIdentityFlush, BeforeCommit }

    private sealed class InterruptAliasCreation(
        IUnitOfWork inner, RomdDbContext db, CancellationTokenSource cancellation, Failure failure) : IUnitOfWork
    {
        public int GeneratedId { get; private set; }
        public TransactionConflict ConflictOf(Exception error) => inner.ConflictOf(error);
        public async Task<ITransaction> BeginTransactionAsync(CancellationToken cancellationToken = default) =>
            new Transaction(await inner.BeginTransactionAsync(cancellationToken), failure);

        public async Task FlushAsync(CancellationToken cancellationToken = default)
        {
            await inner.FlushAsync(cancellationToken);
            GeneratedId = await db.PlatformAliases.Select(row => row.Id).SingleAsync(cancellationToken);
            if (failure == Failure.CancelAfterIdentityFlush)
            {
                await cancellation.CancelAsync();
                cancellationToken.ThrowIfCancellationRequested();
            }
            if (failure == Failure.AfterIdentityFlush)
                throw new IOException("Injected failure after alias identity flush.");
        }

        private sealed class Transaction(ITransaction transaction, Failure failure) : ITransaction
        {
            public async Task CommitAsync(CancellationToken cancellationToken = default)
            {
                if (failure == Failure.BeforeCommit)
                    throw new IOException("Injected alias commit failure.");
                await transaction.CommitAsync(cancellationToken);
            }
            public Task RollbackAsync(CancellationToken cancellationToken = default) => transaction.RollbackAsync(cancellationToken);
            public ValueTask DisposeAsync() => transaction.DisposeAsync();
        }
    }
}
