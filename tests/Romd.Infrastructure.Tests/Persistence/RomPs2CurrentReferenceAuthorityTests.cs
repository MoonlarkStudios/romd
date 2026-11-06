using System.Data;
using System.Data.Common;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using Romd.Admin.Application.Source.Rom;
using Romd.Domain.Hashing;
using Romd.Domain.Source.Rom;
using Romd.Persistence;
using Romd.Persistence.Entities;
using Romd.Persistence.Repositories;
using Romd.Persistence.MediaFacts;
using Romd.PostgreSql.TestSupport;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Persistence;

/// <remarks>Synthetic complete tuples exercise current authority and immutable persistence, not real media qualification.</remarks>
public sealed class RomPs2CurrentReferenceAuthorityTests
{
    private const long LogicalBytes = 131072;
    private static readonly DateTimeOffset InspectedAt = new(2026, 10, 10, 0, 0, 0, TimeSpan.Zero);
    private static readonly byte[] OriginalBody = [2];
    private static readonly byte[] EarlierBody = [1];

    [Fact]
    public async Task ResolveAsync_NoCallerTransaction_RefusesBeforeOpeningConnection()
    {
        await using var db = ClosedContext();
        var error = await Should.ThrowAsync<InvalidOperationException>(() => Authority(db).ResolveAsync(Measurement(), Boot(), InspectedAt));
        error.Message.ShouldContain("caller-owned publication transaction");
        db.Database.GetDbConnection().State.ShouldBe(ConnectionState.Closed);
        db.Database.CurrentTransaction.ShouldBeNull();
    }

    [Fact]
    public async Task ResolveAsync_PreCancelled_PreservesTokenBeforeTransactionAdmission()
    {
        await using var db = ClosedContext();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var error = await Should.ThrowAsync<OperationCanceledException>(() =>
            Authority(db).ResolveAsync(Measurement(), Boot(), InspectedAt, cancellation.Token));
        error.CancellationToken.ShouldBe(cancellation.Token);
        db.Database.GetDbConnection().State.ShouldBe(ConnectionState.Closed);
        db.Database.CurrentTransaction.ShouldBeNull();
    }

    [Fact]
    public async Task ResolveAsync_CallerTransaction_DoesNotFlushCommitOrRetainVerdicts()
    {
        await using var database = PostgreSqlTestDatabase.Create();
        await using var db = database.CreateContext();
        var pending = RomDiscMediaReferenceEntity.FromDomain(Reference(OriginalBody), OriginalBody);
        db.Add(pending);
        await using var transaction = await db.Database.BeginTransactionAsync();
        Guid transactionId = transaction.TransactionId;

        var result = await Authority(db).ResolveAsync(Measurement(), Boot(), InspectedAt);

        result.Value.Status.ShouldBe(RomPs2DvdResolutionStatus.PendingReference);
        result.Value.Eligibility.ShouldBeNull();
        db.Database.CurrentTransaction.ShouldNotBeNull().TransactionId.ShouldBe(transactionId);
        db.Entry(pending).State.ShouldBe(EntityState.Added);
        (await db.Set<RomDiscMediaReferenceEntity>().AsNoTracking().CountAsync()).ShouldBe(0);
        await transaction.RollbackAsync();
        await using var observer = database.CreateContext();
        (await observer.Set<RomDiscMediaReferenceEntity>().CountAsync()).ShouldBe(0);
    }

    [Fact]
    public async Task ResolveAsync_AgreeingEarlierSortingImport_RecomputesCurrentSelectionAndPreservesRawInspection()
    {
        await using var database = PostgreSqlTestDatabase.Create();
        var retained = Reference(OriginalBody);
        var earlier = Reference(EarlierBody);
        (earlier.RawReferenceSha256 < retained.RawReferenceSha256).ShouldBeTrue();
        await ImportAsync(database, retained, OriginalBody);
        var cached = await PublishObservationAsync(database);
        string originalBytes = await HistoricalRowsAsync(database, cached.Id);
        await ImportAsync(database, earlier, EarlierBody);

        await using (var db = database.CreateContext())
        await using (var transaction = await db.Database.BeginTransactionAsync())
        {
            var resolved = await Authority(db).ResolveAsync(Measurement(), Boot(), InspectedAt.AddDays(1));
            resolved.IsError.ShouldBeFalse();
            resolved.Value.Status.ShouldBe(RomPs2DvdResolutionStatus.Supported);
            resolved.Value.Reference.ShouldBe(earlier);
            var current = resolved.Value.Eligibility.ShouldNotBeNull();
            current.InspectedAt.ShouldBe(InspectedAt.AddDays(1));
            current.Ps2DvdFacts!.Boot.ShouldBe(cached.Layout);
            current.Ps2DvdFacts!.DvdReference.ReferenceRecordId.ShouldBe(earlier.Id);
            await transaction.CommitAsync();
        }

        (await HistoricalRowsAsync(database, cached.Id)).ShouldBe(originalBytes);
        await using var observer = database.CreateContext();
        (await observer.Verifications.CountAsync()).ShouldBe(2);
        (await observer.Set<Romd.Persistence.Entities.MediaReconciliationSubjectEntity>().SingleAsync()).RequestedVersion.ShouldBe(2);
        (await observer.Set<RomDiscMediaReferenceEntity>().CountAsync()).ShouldBe(2);
    }

    [Theory]
    [InlineData(RomDiscReferenceMedia.Dvd9)]
    [InlineData(RomDiscReferenceMedia.Cd)]
    public async Task ResolveAsync_PhysicalConflictImportedAfterInspection_RefusesWithoutChangingHistoricalRows(RomDiscReferenceMedia media)
    {
        await using var database = PostgreSqlTestDatabase.Create();
        var original = Reference(OriginalBody);
        await ImportAsync(database, original, OriginalBody);
        var cached = await PublishObservationAsync(database);
        string originalBytes = await HistoricalRowsAsync(database, cached.Id);
        await ImportAsync(database, Reference(EarlierBody, media), EarlierBody);

        await using (var db = database.CreateContext())
        await using (var transaction = await db.Database.BeginTransactionAsync())
        {
            var resolved = await Authority(db).ResolveAsync(Measurement(), Boot(), InspectedAt.AddDays(1));
            resolved.IsError.ShouldBeFalse();
            resolved.Value.Status.ShouldBe(RomPs2DvdResolutionStatus.PendingConflictingReferences);
            resolved.Value.Eligibility.ShouldBeNull();
            resolved.Value.Reference.ShouldBeNull();
            db.ChangeTracker.Entries().ShouldBeEmpty();
            await transaction.CommitAsync();
        }

        (await HistoricalRowsAsync(database, cached.Id)).ShouldBe(originalBytes);
        await using var observer = database.CreateContext();
        (await new RomRetainedLogicalInspectionReader(observer, database.CreateOptions()).ReadAsync(cached.Id))
            .Value.ShouldNotBeNull().Observation.SameObservation(cached).ShouldBeTrue();
        (await observer.Verifications.CountAsync()).ShouldBe(2);
        (await observer.Set<Romd.Persistence.Entities.MediaReconciliationSubjectEntity>().SingleAsync()).RequestedVersion.ShouldBe(2);
    }

    [Theory]
    [InlineData(IsolationLevel.ReadUncommitted)]
    [InlineData(IsolationLevel.RepeatableRead)]
    [InlineData(IsolationLevel.Serializable)]
    public async Task ResolveAsync_WrongCallerIsolation_RefusesBeforeGuardOrQueriesWithoutChangingData(IsolationLevel isolation)
    {
        await using var database = PostgreSqlTestDatabase.Create();
        await ImportAsync(database, Reference(OriginalBody), OriginalBody);
        var cached = await PublishObservationAsync(database);
        string originalBytes = await HistoricalRowsAsync(database, cached.Id);
        var commands = new AuthorityCommandInterceptor();
        await using var db = new RomdDbContext(database.CreateOptions(options => options.AddInterceptors(commands)));
        await using var transaction = await db.Database.BeginTransactionAsync(isolation);
        transaction.GetDbTransaction().IsolationLevel.ShouldBe(isolation);
        Guid transactionId = transaction.TransactionId;
        // Fix the caller's first-read snapshot before a separately committed conflicting import.
        (await db.Set<RomDiscMediaReferenceEntity>().AsNoTracking().CountAsync()).ShouldBe(1);
        await ImportAsync(database, Reference(EarlierBody, RomDiscReferenceMedia.Cd), EarlierBody);
        byte[] pendingBody = [3];
        var pending = RomDiscMediaReferenceEntity.FromDomain(Reference(pendingBody), pendingBody);
        db.Add(pending);
        commands.Commands.Clear();

        var error = await Should.ThrowAsync<InvalidOperationException>(() =>
            Authority(db).ResolveAsync(Measurement(), Boot(), InspectedAt.AddDays(1)));

        error.Message.ShouldContain("caller-owned ReadCommitted publication transaction");
        // Capture every EF command kind: the guard is non-query SQL; cache/reference reads are readers.
        commands.Commands.ShouldBeEmpty();
        db.Database.CurrentTransaction.ShouldNotBeNull().TransactionId.ShouldBe(transactionId);
        transaction.GetDbTransaction().IsolationLevel.ShouldBe(isolation);
        db.ChangeTracker.Entries().ShouldHaveSingleItem().Entity.ShouldBeSameAs(pending);
        db.Entry(pending).State.ShouldBe(EntityState.Added);
        await transaction.RollbackAsync();
        (await HistoricalRowsAsync(database, cached.Id)).ShouldBe(originalBytes);
        await using var observer = database.CreateContext();
        (await observer.Set<RomDiscMediaReferenceEntity>().CountAsync()).ShouldBe(2);
        (await observer.Verifications.CountAsync()).ShouldBe(2);
        (await observer.Set<Romd.Persistence.Entities.MediaReconciliationSubjectEntity>().SingleAsync()).RequestedVersion.ShouldBe(2);
    }

    [Fact]
    public async Task ResolveAsync_DefaultReadCommitted_ObservesConflictCommittedAfterItsFirstRead()
    {
        await using var database = PostgreSqlTestDatabase.Create();
        await ImportAsync(database, Reference(OriginalBody), OriginalBody);
        var cached = await PublishObservationAsync(database);
        string originalBytes = await HistoricalRowsAsync(database, cached.Id);
        await using var db = database.CreateContext();
        await using var transaction = await db.Database.BeginTransactionAsync();
        transaction.GetDbTransaction().IsolationLevel.ShouldBe(IsolationLevel.ReadCommitted);
        Guid transactionId = transaction.TransactionId;
        (await db.Set<RomDiscMediaReferenceEntity>().AsNoTracking().CountAsync()).ShouldBe(1);
        await ImportAsync(database, Reference(EarlierBody, RomDiscReferenceMedia.Cd), EarlierBody);

        var resolved = await Authority(db).ResolveAsync(Measurement(), Boot(), InspectedAt.AddDays(1));

        resolved.IsError.ShouldBeFalse();
        resolved.Value.Status.ShouldBe(RomPs2DvdResolutionStatus.PendingConflictingReferences);
        resolved.Value.Eligibility.ShouldBeNull();
        resolved.Value.Reference.ShouldBeNull();
        db.Database.CurrentTransaction.ShouldNotBeNull().TransactionId.ShouldBe(transactionId);
        db.ChangeTracker.Entries().ShouldBeEmpty();
        await transaction.CommitAsync();
        (await HistoricalRowsAsync(database, cached.Id)).ShouldBe(originalBytes);
        await using var observer = database.CreateContext();
        (await observer.Set<RomDiscMediaReferenceEntity>().CountAsync()).ShouldBe(2);
        (await observer.Verifications.CountAsync()).ShouldBe(2);
        (await observer.Set<Romd.Persistence.Entities.MediaReconciliationSubjectEntity>().SingleAsync()).RequestedVersion.ShouldBe(2);
    }

    private static RomPs2CurrentReferenceAuthority Authority(RomdDbContext context) =>
        new(context, new RomPs2DvdEligibilityResolver(new RomDiscMediaReferenceStore(context)));

    private static async Task ImportAsync(PostgreSqlTestDatabase database, RomDiscMediaReferenceRecord reference, byte[] raw)
    {
        await using var db = database.CreateContext();
        await using var transaction = await db.Database.BeginTransactionAsync();
        (await new RomDiscMediaReferenceImportStore(db, new Romd.Persistence.Repositories.Reconciliation.MediaReconciliationRequests(db, TimeProvider.System)).StageAsync(reference, raw)).Value.ShouldBe(reference);
        await db.SaveChangesAsync();
        await transaction.CommitAsync();
    }

    private static async Task<RomLogicalInspection> PublishObservationAsync(PostgreSqlTestDatabase database)
    {
        await using var db = database.CreateContext();
        return (await LogicalInspectionFixture.PublishAsync(db, InspectedAt, true)).Observation;
    }

    private static async Task<string> HistoricalRowsAsync(PostgreSqlTestDatabase database, Guid id)
    {
        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("""
            SELECT jsonb_build_array(to_jsonb(inspection), to_jsonb(boot))::text
            FROM romd."LogicalInspections" inspection
            JOIN romd."Ps2BootObservations" boot ON boot."InspectionId"=inspection."Id"
            WHERE inspection."Id"=$1
            """, connection);
        command.Parameters.AddWithValue(id);
        return ((string?)await command.ExecuteScalarAsync()).ShouldNotBeNull();
    }

    private sealed class AuthorityCommandInterceptor : DbCommandInterceptor
    {
        public List<string> Commands { get; } = [];

        public override InterceptionResult<DbDataReader> ReaderExecuting(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result)
        {
            Commands.Add(command.CommandText);
            return result;
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            Commands.Add(command.CommandText);
            return ValueTask.FromResult(result);
        }

        public override InterceptionResult<object> ScalarExecuting(DbCommand command,
            CommandEventData eventData, InterceptionResult<object> result)
        {
            Commands.Add(command.CommandText);
            return result;
        }

        public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<object> result, CancellationToken cancellationToken = default)
        {
            Commands.Add(command.CommandText);
            return ValueTask.FromResult(result);
        }

        public override InterceptionResult<int> NonQueryExecuting(DbCommand command,
            CommandEventData eventData, InterceptionResult<int> result)
        {
            Commands.Add(command.CommandText);
            return result;
        }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            Commands.Add(command.CommandText);
            return ValueTask.FromResult(result);
        }
    }

    private static RomdDbContext ClosedContext() => new(new DbContextOptionsBuilder<RomdDbContext>()
        .UseNpgsql("Host=localhost;Database=romd;Username=romd").Options);

    private static RomLogicalIdentity Measurement() => LogicalInspectionFixture.Identity;
    private static RomPs2BootLayoutFacts Boot() => LogicalInspectionFixture.Boot;

    private static RomDiscMediaReferenceRecord Reference(byte[] raw, RomDiscReferenceMedia media = RomDiscReferenceMedia.Dvd5)
    {
        var measured = Measurement();
        return new(Guid.NewGuid(), RomDiscReferenceSystem.PlayStation2, media, 1, measured.Size, measured.Sha1,
            measured.Md5, measured.Crc32, raw.LongLength, Sha256.FromSpan(SHA256.HashData(raw)), "synthetic-raw-reference",
            "synthetic-reference/v1", "caller-admitted-unit-fixture/v1", InspectedAt);
    }
}
