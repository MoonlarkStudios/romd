using System.Data;
using System.Data.Common;
using System.Reflection;
using System.Security.Cryptography;
using ErrorOr;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
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

/// <remarks>Database cases use the migrated PostgreSQL fixture after root's entity/model/migration integration.
/// The synthetic admitted record is shape only; neither the port nor these tests authenticate retrieval.</remarks>
public sealed class RomDiscMediaReferenceImportStoreTests
{
    private static readonly Guid Identity = Guid.Parse("11111111-2222-3333-4444-555555555555");
    private static readonly byte[] Body = [1, 0, 255];
    private const long SubMillisecondTicks = 621355968000001234;

    [Fact]
    public async Task GetRuntimeRoleAsync_ActualCredential_ReadsWithoutTransactionOrWrites()
    {
        await using var database = PostgreSqlTestDatabase.Create();
        var commands = new CommandObserver();
        await using var db = Context(database, commands);
        string expected = new NpgsqlConnectionStringBuilder(database.ConnectionString).Username!;
        (await Store(db).GetRuntimeRoleAsync()).ShouldBe(expected);
        commands.RoleReads.ShouldBe(1);
        commands.Reads.ShouldBe(0);
        commands.Writes.ShouldBe(0);
        db.Database.CurrentTransaction.ShouldBeNull();
        db.ChangeTracker.Entries().ShouldBeEmpty();
    }

    [Fact]
    public async Task GetRuntimeRoleAsync_PreCancelled_PreservesTokenBeforeOpeningConnection()
    {
        await using var db = ClosedContext();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await AssertCancellation(Store(db).GetRuntimeRoleAsync(cancellation.Token), cancellation.Token);
        db.Database.GetDbConnection().State.ShouldBe(ConnectionState.Closed);
        db.Database.CurrentTransaction.ShouldBeNull();
    }

    [Fact]
    public async Task StageAsync_NoAmbientTransaction_RefusesBeforeOpeningConnection()
    {
        await using var db = ClosedContext();
        var error = await Should.ThrowAsync<InvalidOperationException>(() =>
            Store(db).StageAsync(Record(), Body));
        error.Message.ShouldContain("caller-owned publication transaction");
        db.Database.GetDbConnection().State.ShouldBe(ConnectionState.Closed);
        db.Database.CurrentTransaction.ShouldBeNull();
    }

    [Fact]
    public async Task StageAsync_PreCancelled_PreservesCallerTokenBeforeTransactionAdmission()
    {
        await using var db = ClosedContext();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await AssertCancellation(Store(db).StageAsync(Record(), Body, cancellation.Token), cancellation.Token);
        db.Database.GetDbConnection().State.ShouldBe(ConnectionState.Closed);
        db.Database.CurrentTransaction.ShouldBeNull();
    }

    [Theory]
    [InlineData("length")]
    [InlineData("hash")]
    public async Task StageAsync_SeparatedRawCustody_RefusesBeforeInsert(string changed)
    {
        await using var database = PostgreSqlTestDatabase.Create();
        var commands = new CommandObserver();
        await using var db = Context(database, commands);
        await using var transaction = await db.Database.BeginTransactionAsync();
        byte[] raw = changed == "length" ? Body[..^1] : Body.ToArray();
        if (changed == "hash")
            raw[0] ^= 127;
        Exception? observed = null;
        try
        { await Store(db).StageAsync(Record(), raw); }
        catch (Exception failure) { observed = failure; }
        if (changed == "length")
            observed.ShouldBeOfType<ArgumentException>();
        else
            observed.ShouldBeOfType<InvalidOperationException>();
        commands.Writes.ShouldBe(0);
        commands.Reads.ShouldBe(0);
        await transaction.RollbackAsync();
        await using var observer = database.CreateContext();
        (await observer.Set<RomDiscMediaReferenceEntity>().CountAsync()).ShouldBe(0);
    }

    [Fact]
    public async Task StageAsync_CallerTransaction_DoesNotFlushCommitOrReplaceItsScope()
    {
        await using var database = PostgreSqlTestDatabase.Create();
        await using var db = database.CreateContext();
        var pending = RomDiscMediaReferenceEntity.FromDomain(Record(id: Guid.NewGuid()), Body);
        db.Add(pending);
        await using (var transaction = await db.Database.BeginTransactionAsync())
        {
            Guid originalTransaction = transaction.TransactionId;
            var staged = await Store(db).StageAsync(Record(), Body);
            staged.Value.ShouldBe(Record());
            db.Database.CurrentTransaction.ShouldNotBeNull().TransactionId.ShouldBe(originalTransaction);
            db.Entry(pending).State.ShouldBe(EntityState.Added);
            (await db.Set<RomDiscMediaReferenceEntity>().AsNoTracking().CountAsync()).ShouldBe(1);
            await using var observer = database.CreateContext();
            (await observer.Set<RomDiscMediaReferenceEntity>().CountAsync()).ShouldBe(0);
            await transaction.RollbackAsync();
        }
        await using var afterRollback = database.CreateContext();
        (await afterRollback.Set<RomDiscMediaReferenceEntity>().CountAsync()).ShouldBe(0);
    }

    [Theory]
    [InlineData(0u, 621355968000001234L, -5)]
    [InlineData(uint.MaxValue, 621355967999999999L, 5)]
    [InlineData(0u, 0L, 0)]
    [InlineData(uint.MaxValue, 3155378975999999999L, 0)]
    public async Task StageAsync_CallerCommit_PersistsUnsignedCrcAndExactUtcTicks(uint crc, long ticks, int offsetHours)
    {
        await using var database = PostgreSqlTestDatabase.Create();
        await using var db = database.CreateContext();
        var record = Record(crc: crc, time: new DateTimeOffset(ticks, TimeSpan.Zero).ToOffset(TimeSpan.FromHours(offsetHours)));
        await using (var transaction = await new EfUnitOfWork(db).BeginTransactionAsync())
        {
            (await Store(db).StageAsync(record, Body)).Value.ShouldBe(record);
            await transaction.CommitAsync();
        }
        await using var observer = database.CreateContext();
        var retained = await observer.Set<RomDiscMediaReferenceEntity>().AsNoTracking().SingleAsync();
        retained.ToDomain().ShouldBe(record);
        retained.GetRawBodySnapshot().ShouldBe(Body);
        retained.RetrievedAtUtcTicks.ShouldBe(ticks);
        retained.ToDomain().RetrievedAt.Offset.ShouldBe(TimeSpan.Zero);
        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("SELECT \"Crc32\", \"RetrievedAtUtcTicks\" FROM romd.\"RomDiscMediaReferences\" WHERE \"Id\" = $1", connection);
        command.Parameters.AddWithValue(record.Id);
        await using var reader = await command.ExecuteReaderAsync();
        (await reader.ReadAsync()).ShouldBeTrue();
        reader.GetInt64(0).ShouldBe((long)crc);
        reader.GetInt64(1).ShouldBe(ticks);
    }

    [Fact]
    public async Task StageAsync_SamePreparedReplayAcrossTransactions_RetainsEveryOriginalFactAndBody()
    {
        await using var database = PostgreSqlTestDatabase.Create();
        var record = Record();
        await using (var writer = database.CreateContext())
        await using (var transaction = await writer.Database.BeginTransactionAsync())
        {
            (await Store(writer).StageAsync(record, Body)).Value.ShouldBe(record);
            await transaction.CommitAsync();
        }
        await using var db = database.CreateContext();
        await using var replayTransaction = await db.Database.BeginTransactionAsync();
        var store = Store(db);
        (await store.StageAsync(record, Body.ToArray())).Value.ShouldBe(record);
        (await store.StageAsync(record, Body.ToArray())).Value.ShouldBe(record);
        await replayTransaction.CommitAsync();
        await using var observer = database.CreateContext();
        var retained = (await observer.Set<RomDiscMediaReferenceEntity>().ToListAsync()).ShouldHaveSingleItem();
        retained.ToDomain().ShouldBe(record);
        retained.GetRawBodySnapshot().ShouldBe(Body);
    }

    [Theory]
    [InlineData("system")]
    [InlineData("media")]
    [InlineData("file-count")]
    [InlineData("logical-size")]
    [InlineData("sha1")]
    [InlineData("md5")]
    [InlineData("crc")]
    [InlineData("raw-size")]
    [InlineData("raw-body")]
    [InlineData("source")]
    [InlineData("profile")]
    [InlineData("provenance")]
    [InlineData("timestamp")]
    public async Task StageAsync_ForeignFactsReuseGuid_ConflictsWithoutRewritingRetainedCustody(string changed)
    {
        await using var database = PostgreSqlTestDatabase.Create();
        var original = Record();
        await using var db = database.CreateContext();
        await using var transaction = await db.Database.BeginTransactionAsync();
        var store = Store(db);
        (await store.StageAsync(original, Body)).IsError.ShouldBeFalse();
        byte[] raw = changed == "raw-size" ? [1, 0, 255, 2] : Body.ToArray();
        if (changed == "raw-body")
            raw[0] ^= 127;
        var proposed = ChangedRecord(original, changed, raw);
        var conflict = await store.StageAsync(proposed, raw);
        conflict.FirstError.ShouldBe(RomDiscMediaReferenceImportErrors.ConflictingIdentity());
        await transaction.CommitAsync();
        await using var observer = database.CreateContext();
        var retained = (await observer.Set<RomDiscMediaReferenceEntity>().ToListAsync()).ShouldHaveSingleItem();
        retained.ToDomain().ShouldBe(original);
        retained.GetRawBodySnapshot().ShouldBe(Body);
    }

    [Fact]
    public async Task StageAsync_DifferentGuidSamePublishedQuartet_RetainsContradictoryMediaForResolver()
    {
        await using var database = PostgreSqlTestDatabase.Create();
        await using var db = database.CreateContext();
        await using var transaction = await db.Database.BeginTransactionAsync();
        var first = Record();
        var second = Record(id: Guid.NewGuid(), media: RomDiscReferenceMedia.Dvd9,
            time: first.RetrievedAt.AddTicks(1));
        var store = Store(db);
        (await store.StageAsync(first, Body)).Value.ShouldBe(first);
        (await store.StageAsync(second, Body)).Value.ShouldBe(second);
        await transaction.CommitAsync();
        await using var observer = database.CreateContext();
        var rows = await observer.Set<RomDiscMediaReferenceEntity>().ToListAsync();
        rows.Count.ShouldBe(2);
        rows.Select(row => row.ToDomain()).ShouldContain(first);
        rows.Select(row => row.ToDomain()).ShouldContain(second);
        rows.ShouldAllBe(row => row.LogicalBytes == first.LogicalBytes && row.Sha1 == first.Sha1 &&
            row.Md5 == first.Md5 && row.Crc32 == first.Crc32.ToUInt32());
    }

    [Fact]
    public async Task StageAsync_CallerRawAndReturnedSnapshotMutation_CannotChangeRetainedBody()
    {
        await using var database = PostgreSqlTestDatabase.Create();
        byte[] raw = Body.ToArray();
        var record = Record(raw: raw);
        var commands = new CommandObserver { BeforeInsert = () => raw[0] ^= 127 };
        await using var db = Context(database, commands);
        await using var transaction = await db.Database.BeginTransactionAsync();
        (await Store(db).StageAsync(record, raw)).Value.ShouldBe(record);
        raw.ShouldNotBe(Body);
        commands.Writes.ShouldBe(1);
        var retained = await db.Set<RomDiscMediaReferenceEntity>().AsNoTracking().SingleAsync();
        byte[] copy = retained.GetRawBodySnapshot();
        copy[0] ^= 63;
        await transaction.CommitAsync();
        await using var observer = database.CreateContext();
        var durable = await observer.Set<RomDiscMediaReferenceEntity>().SingleAsync();
        durable.GetRawBodySnapshot().ShouldBe(Body);
        durable.ToDomain().ShouldBe(record);
    }

    [Fact]
    public async Task StageAsync_TrackedUnsavedForeignFacts_DoNotOverrideExactStoredReplay()
    {
        await using var database = PostgreSqlTestDatabase.Create();
        await using var db = database.CreateContext();
        await using var transaction = await db.Database.BeginTransactionAsync();
        var record = Record();
        var store = Store(db);
        (await store.StageAsync(record, Body)).IsError.ShouldBeFalse();
        var tracked = await db.Set<RomDiscMediaReferenceEntity>().AsTracking().SingleAsync();
        db.Entry(tracked).Property(row => row.ReferenceSource).CurrentValue = "unsaved-local-alias";
        (await store.StageAsync(record, Body)).Value.ShouldBe(record);
        db.ChangeTracker.Entries<RomDiscMediaReferenceEntity>().ShouldHaveSingleItem().Entity.ShouldBeSameAs(tracked);
        db.Entry(tracked).Property(row => row.ReferenceSource).CurrentValue.ShouldBe("unsaved-local-alias");
        await transaction.RollbackAsync();
    }

    [Theory]
    [InlineData("insert", "npgsql-transient")]
    [InlineData("insert", "timeout")]
    [InlineData("reload", "npgsql-transient")]
    [InlineData("reload", "timeout")]
    [InlineData("guard", "npgsql-transient")]
    [InlineData("guard", "timeout")]
    [InlineData("subjects", "npgsql-transient")]
    [InlineData("subjects", "timeout")]
    public async Task StageAsync_TransientCommandFailure_ReturnsUnavailableAndLeavesRollbackToCaller(string phase, string kind)
    {
        await using var database = PostgreSqlTestDatabase.Create();
        var commands = FailingCommand(phase, Failure(kind));
        await using var db = Context(database, commands);
        await using (var transaction = await db.Database.BeginTransactionAsync())
        {
            (await Store(db).StageAsync(Record(), Body)).FirstError
                .ShouldBe(RomDiscMediaReferenceImportErrors.Unavailable());
            db.Database.CurrentTransaction.ShouldBeSameAs(transaction);
            commands.Writes.ShouldBe(phase == "guard" ? 0 : 1);
            commands.Reads.ShouldBe(phase is "reload" or "subjects" ? 1 : 0);
            await transaction.RollbackAsync();
        }
        await using var observer = database.CreateContext();
        (await observer.Set<RomDiscMediaReferenceEntity>().CountAsync()).ShouldBe(0);
    }

    [Theory]
    [InlineData("insert", "npgsql-nontransient")]
    [InlineData("insert", "constraint")]
    [InlineData("insert", "logic")]
    [InlineData("reload", "npgsql-nontransient")]
    [InlineData("reload", "constraint")]
    [InlineData("reload", "logic")]
    [InlineData("insert", "logic-timeout")]
    [InlineData("reload", "logic-timeout")]
    [InlineData("insert", "wrapped-npgsql-nontransient")]
    [InlineData("reload", "wrapped-npgsql-nontransient")]
    [InlineData("guard", "npgsql-nontransient")]
    [InlineData("guard", "constraint")]
    [InlineData("guard", "logic")]
    [InlineData("guard", "logic-timeout")]
    [InlineData("guard", "wrapped-npgsql-nontransient")]
    [InlineData("subjects", "npgsql-nontransient")]
    [InlineData("subjects", "constraint")]
    [InlineData("subjects", "logic")]
    [InlineData("subjects", "logic-timeout")]
    [InlineData("subjects", "wrapped-npgsql-nontransient")]
    public async Task StageAsync_NontransientOrLogicFailure_PropagatesOriginalException(string phase, string kind)
    {
        await using var database = PostgreSqlTestDatabase.Create();
        var injected = Failure(kind);
        var commands = FailingCommand(phase, injected);
        await using var db = Context(database, commands);
        await using var transaction = await db.Database.BeginTransactionAsync();
        Exception? observed = null;
        try
        { await Store(db).StageAsync(Record(), Body); }
        catch (Exception failure) { observed = failure; }
        observed.ShouldBeSameAs(injected);
        commands.Writes.ShouldBe(phase == "guard" ? 0 : 1);
        commands.Reads.ShouldBe(phase is "reload" or "subjects" ? 1 : 0);
        await transaction.RollbackAsync();
    }

    [Theory]
    [InlineData("insert", "npgsql-transient")]
    [InlineData("insert", "timeout")]
    [InlineData("reload", "npgsql-transient")]
    [InlineData("reload", "timeout")]
    [InlineData("guard", "npgsql-transient")]
    [InlineData("guard", "timeout")]
    [InlineData("subjects", "npgsql-transient")]
    [InlineData("subjects", "timeout")]
    public async Task StageAsync_CancellationDuringTransientFailure_TakesPrecedenceWithExactCallerToken(string phase, string kind)
    {
        await using var database = PostgreSqlTestDatabase.Create();
        using var cancellation = new CancellationTokenSource();
        var commands = FailingCommand(phase, Failure(kind), cancellation.Cancel);
        await using var db = Context(database, commands);
        await using var transaction = await db.Database.BeginTransactionAsync();
        await AssertCancellation(Store(db).StageAsync(Record(), Body, cancellation.Token), cancellation.Token);
        commands.Writes.ShouldBe(phase == "guard" ? 0 : 1);
        commands.Reads.ShouldBe(phase is "reload" or "subjects" ? 1 : 0);
        await transaction.RollbackAsync(CancellationToken.None);
        await using var observer = database.CreateContext();
        (await observer.Set<RomDiscMediaReferenceEntity>().CountAsync()).ShouldBe(0);
    }

    [Theory]
    [InlineData("insert")]
    [InlineData("reload")]
    [InlineData("guard")]
    [InlineData("subjects")]
    public async Task StageAsync_CommandCancellation_PropagatesOriginalExceptionIdentity(string phase)
    {
        await using var database = PostgreSqlTestDatabase.Create();
        using var cancellation = new CancellationTokenSource();
        var injected = new OperationCanceledException(cancellation.Token);
        var commands = FailingCommand(phase, injected, cancellation.Cancel);
        await using var db = Context(database, commands);
        await using var transaction = await db.Database.BeginTransactionAsync();
        Exception? observed = null;
        try
        { await Store(db).StageAsync(Record(), Body, cancellation.Token); }
        catch (Exception failure) { observed = failure; }
        observed.ShouldBeSameAs(injected);
        commands.Writes.ShouldBe(phase == "guard" ? 0 : 1);
        commands.Reads.ShouldBe(phase is "reload" or "subjects" ? 1 : 0);
        await transaction.RollbackAsync(CancellationToken.None);
    }

    [Fact]
    public async Task StageAsync_CorruptedMaterialization_PropagatesCustodyFailureInsteadOfBusinessResult()
    {
        await using var database = PostgreSqlTestDatabase.Create();
        var corruption = new CorruptMaterializedBody();
        await using var db = new RomdDbContext(database.CreateOptions(options => options.AddInterceptors(corruption)));
        await using var transaction = await db.Database.BeginTransactionAsync();
        var failure = await Should.ThrowAsync<InvalidOperationException>(() =>
            Store(db).StageAsync(Record(), Body));
        failure.Message.ShouldContain("retained reference body");
        corruption.RowsMutated.ShouldBe(1);
        await transaction.RollbackAsync();
        await using var observer = database.CreateContext();
        (await observer.Set<RomDiscMediaReferenceEntity>().CountAsync()).ShouldBe(0);
        // Only the materialized object is corrupted; migrated SQL body checks remain in force.
    }

    [Fact]
    public async Task AssertionGuard_NoCallerTransaction_RefusesBeforeOpeningConnection()
    {
        await using var db = ClosedContext();
        var failure = await Should.ThrowAsync<InvalidOperationException>(() =>
            RomPs2ReferenceAssertionLock.AcquireAsync(db, Query(Record()), CancellationToken.None));
        failure.Message.ShouldContain("caller's transaction");
        db.Database.GetDbConnection().State.ShouldBe(ConnectionState.Closed);
        db.ChangeTracker.Entries().ShouldBeEmpty();
    }

    [Fact]
    public async Task AssertionGuard_PreCancelled_PreservesTokenBeforeTransactionOrConnection()
    {
        await using var db = ClosedContext();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var failure = await Should.ThrowAsync<OperationCanceledException>(() =>
            RomPs2ReferenceAssertionLock.AcquireAsync(db, Query(Record()), cancellation.Token));
        failure.CancellationToken.ShouldBe(cancellation.Token);
        db.Database.GetDbConnection().State.ShouldBe(ConnectionState.Closed);
        db.Database.CurrentTransaction.ShouldBeNull();
    }

    [Theory]
    [InlineData(0u, -677253038)]
    [InlineData(42u, -1845898208)]
    [InlineData(uint.MaxValue, 1451649330)]
    public async Task AssertionGuard_CanonicalPublishedTuple_HoldsExactFamily149KeyThroughCallerTransaction(uint crc, int expectedKey)
    {
        await using var database = PostgreSqlTestDatabase.Create();
        await using var db = database.CreateContext();
        await using var transaction = await db.Database.BeginTransactionAsync();
        await RomPs2ReferenceAssertionLock.AcquireAsync(db, Query(Record(crc: crc)), CancellationToken.None);
        long oid = unchecked((uint)expectedKey);
        // Independent fixed vectors include initialized zero and unsigned maximum CRC; no test recomputes the production key.
        int held = await db.Database.SqlQuery<int>($"""
            SELECT count(*)::int AS "Value" FROM pg_locks
            WHERE locktype = 'advisory' AND pid = pg_backend_pid() AND granted
                AND classid::bigint = 149 AND objid::bigint = {oid} AND objsubid = 2
            """).SingleAsync();
        held.ShouldBe(1);
        db.Database.CurrentTransaction.ShouldBeSameAs(transaction);
        db.ChangeTracker.Entries().ShouldBeEmpty();
        await transaction.RollbackAsync();
        int released = await db.Database.SqlQuery<int>($"""
            SELECT count(*)::int AS "Value" FROM pg_locks
            WHERE locktype = 'advisory' AND pid = pg_backend_pid() AND granted
                AND classid::bigint = 149 AND objid::bigint = {oid} AND objsubid = 2
            """).SingleAsync();
        released.ShouldBe(0);
    }

    [Theory]
    [InlineData(IsolationLevel.ReadUncommitted, "guard")]
    [InlineData(IsolationLevel.RepeatableRead, "guard")]
    [InlineData(IsolationLevel.Serializable, "guard")]
    [InlineData(IsolationLevel.ReadUncommitted, "import")]
    [InlineData(IsolationLevel.RepeatableRead, "import")]
    [InlineData(IsolationLevel.Serializable, "import")]
    public async Task AssertionPublication_UnsupportedIsolation_RefusesBeforeGuardReferenceOrWakeupWrites(IsolationLevel isolation, string operation)
    {
        await using var database = PostgreSqlTestDatabase.Create();
        var commands = new CommandObserver();
        await using var db = Context(database, commands);
        await using var transaction = await db.Database.BeginTransactionAsync(isolation);
        var record = Record();
        Func<Task> call = operation switch
        {
            "guard" => () => RomPs2ReferenceAssertionLock.AcquireAsync(db, Query(record), CancellationToken.None),
            "import" => () => Store(db).StageAsync(record, Body),
            _ => throw new ArgumentOutOfRangeException(nameof(operation))
        };
        (await Should.ThrowAsync<InvalidOperationException>(call)).Message.ShouldContain("ReadCommitted");
        commands.Reads.ShouldBe(0);
        commands.Writes.ShouldBe(0);
        commands.Guards.ShouldBe(0);
        db.Database.CurrentTransaction.ShouldBeSameAs(transaction);
        (await db.RomDiscMediaReferences.CountAsync()).ShouldBe(0);
        (await db.Set<MediaReconciliationRequestEntity>().CountAsync()).ShouldBe(0);
        await transaction.RollbackAsync();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task StageAsync_AbsentObservationAndReference_RealStoresSerializeBothPublicationOrders(bool bootFirst)
    {
        await using var database = PostgreSqlTestDatabase.Create();
        var record = Record(crc: 0, logicalBytes: 131072);
        var owner = await SeedBootOwnerAsync(database, record);
        Guid inspectionId = Guid.NewGuid();
        var importCommands = new CommandObserver();
        await using var importDb = Context(database, importCommands);
        await using var bootDb = database.CreateContext();
        await importDb.Database.OpenConnectionAsync();
        await bootDb.Database.OpenConnectionAsync();
        int importPid = await importDb.Database.SqlQueryRaw<int>("SELECT pg_backend_pid() AS \"Value\"").SingleAsync();
        int bootPid = await bootDb.Database.SqlQueryRaw<int>("SELECT pg_backend_pid() AS \"Value\"").SingleAsync();
        importPid.ShouldNotBe(bootPid);
        await using var importTransaction = await new EfUnitOfWork(importDb).BeginTransactionAsync();
        await using var bootTransaction = await new EfUnitOfWork(bootDb).BeginTransactionAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        Task<ErrorOr<RomDiscMediaReferenceRecord>>? import = null;
        Task? boot = null;
        try
        {
            await using (var before = database.CreateContext())
            {
                (await before.RomDiscMediaReferences.CountAsync()).ShouldBe(0);
                (await InspectionIdsAsync(before)).Count.ShouldBe(0);
                (await before.Set<MediaReconciliationRequestEntity>().CountAsync()).ShouldBe(0);
            }
            if (bootFirst)
            {
                await StageCallerBootProtocolAsync(bootDb, owner, record, inspectionId, deadline.Token);
                import = Store(importDb).StageAsync(record, Body, deadline.Token);
                await AssertWaitingForTupleGuardAsync(database, importPid, deadline.Token);
                // A fanout-only guard would wait after its INSERT; this observer proves the insertion has not begun.
                importCommands.Writes.ShouldBe(0);
                await bootTransaction.CommitAsync(deadline.Token);
                (await import).Value.ShouldBe(record);
                importDb.Set<MediaReconciliationSubjectEntity>().Local.ShouldHaveSingleItem().RequestedVersion.ShouldBe(2);
                await importTransaction.CommitAsync(deadline.Token);
            }
            else
            {
                (await Store(importDb).StageAsync(record, Body, deadline.Token)).Value.ShouldBe(record);
                importDb.Set<MediaReconciliationSubjectEntity>().Local.ShouldHaveSingleItem().RequestedVersion.ShouldBe(1);
                importDb.Set<MediaReconciliationRequestEntity>().Local.ShouldHaveSingleItem().ConversionRequested.ShouldBeFalse();
                boot = StageCallerBootProtocolAsync(bootDb, owner, record, inspectionId, deadline.Token);
                await AssertWaitingForTupleGuardAsync(database, bootPid, deadline.Token);
                await using (var waiting = database.CreateContext())
                {
                    (await waiting.RomDiscMediaReferences.CountAsync()).ShouldBe(0);
                    (await InspectionIdsAsync(waiting)).Count.ShouldBe(0);
                    (await waiting.Set<MediaReconciliationRequestEntity>().CountAsync()).ShouldBe(0);
                }
                await importTransaction.CommitAsync(deadline.Token);
                await boot;
                await bootTransaction.CommitAsync(deadline.Token);
            }
            await using var observer = database.CreateContext();
            (await observer.RomDiscMediaReferences.SingleAsync()).ToDomain().ShouldBe(record);
            (await InspectionIdsAsync(observer)).ShouldHaveSingleItem().ShouldBe(inspectionId);
            var demand = await observer.Set<MediaReconciliationSubjectEntity>().SingleAsync();
            demand.SubjectKind.ShouldBe("LogicalRom");
            demand.SubjectId.ShouldBe(owner.RomId);
            demand.RequestedVersion.ShouldBe(2);
            demand.ConversionRequested.ShouldBeFalse();
            var audit = await observer.Set<MediaReconciliationRequestEntity>().OrderBy(row => row.RequestedVersion).ToArrayAsync();
            audit.Length.ShouldBe(2);
            audit.ShouldContain(row => row.Id == inspectionId);
            audit.All(row => row.JobId == demand.LatestJobId && row.Cause == "Facts" && !row.ConversionRequested).ShouldBeTrue();
            (await observer.JobDispatches.CountAsync()).ShouldBe(1);
        }
        finally
        {
            // Join cancellation before rollback on either connection; no in-flight command races its own transaction cleanup.
            deadline.Cancel();
            try
            {
                await Task.WhenAll(JoinCancelledAsync(boot ?? Task.CompletedTask),
                    JoinCancelledAsync(import is null ? Task.CompletedTask : import));
            }
            finally
            {
                await bootTransaction.RollbackAsync(CancellationToken.None);
                await importTransaction.RollbackAsync(CancellationToken.None);
            }
        }
    }

    [Fact]
    public async Task StageAsync_ImmutableConflict_DoesNotFanoutAndCallerRollbackRemovesIntermediateWakeup()
    {
        await using var database = PostgreSqlTestDatabase.Create();
        var original = Record(crc: 0, logicalBytes: 131072);
        var owner = await SeedBootOwnerAsync(database, original);
        Guid inspectionId = Guid.NewGuid();
        await using (var seed = database.CreateContext())
        await using (var transaction = await new EfUnitOfWork(seed).BeginTransactionAsync())
        {
            await StageCallerBootProtocolAsync(seed, owner, original, inspectionId, CancellationToken.None);
            // Raw facts and reference changes each stage general demand in the same caller transaction.
            (await Store(seed).StageAsync(original, Body)).Value.ShouldBe(original);
            await transaction.CommitAsync();
        }
        await using (var db = database.CreateContext())
        await using (var transaction = await new EfUnitOfWork(db).BeginTransactionAsync())
        {
            var additional = Record(id: Guid.NewGuid(), crc: 0, logicalBytes: 131072);
            (await Store(db).StageAsync(additional, Body)).Value.ShouldBe(additional);
            db.Set<MediaReconciliationSubjectEntity>().Local.ShouldHaveSingleItem().RequestedVersion.ShouldBe(3);
            var conflicting = Record(crc: 0, logicalBytes: 131072, media: RomDiscReferenceMedia.Dvd9);
            (await Store(db).StageAsync(conflicting, Body)).FirstError.ShouldBe(RomDiscMediaReferenceImportErrors.ConflictingIdentity());
            db.Set<MediaReconciliationSubjectEntity>().Local.ShouldHaveSingleItem().RequestedVersion.ShouldBe(3);
            await transaction.RollbackAsync();
        }
        await using var observer = database.CreateContext();
        (await observer.RomDiscMediaReferences.SingleAsync()).ToDomain().ShouldBe(original);
        (await InspectionIdsAsync(observer)).ShouldHaveSingleItem().ShouldBe(inspectionId);
        (await observer.Set<MediaReconciliationSubjectEntity>().SingleAsync()).RequestedVersion.ShouldBe(2);
    }

    private static RomDiscMediaReferenceImportStore Store(RomdDbContext db) =>
        new(db, new Romd.Persistence.Repositories.Reconciliation.MediaReconciliationRequests(db, TimeProvider.System));
    private static RomDiscMediaReferenceQuery Query(RomDiscMediaReferenceRecord record) =>
        new(record.LogicalBytes, record.Sha1, record.Md5, record.Crc32);

    private static async Task<(int RomId, RomRetainedMeasurement Retained)> SeedBootOwnerAsync(PostgreSqlTestDatabase database, RomDiscMediaReferenceRecord record)
    {
        await using var db = database.CreateContext();
        var content = Sha256.Parse(new string('a', 64));
        db.Files.Add(new() { Id = 1, Sha256 = content, Size = record.LogicalBytes, SizeOnDisk = record.LogicalBytes });
        db.StageOriginalRom(new()
        {
            Id = 1,
            FileId = 1,
            OriginalFilename = "synthetic.iso",
            Sha1 = record.Sha1,
            Md5 = record.Md5,
            Crc32 = record.Crc32,
            CreatedAt = new DateTimeOffset(SubMillisecondTicks, TimeSpan.Zero)
        });
        await db.PublishOriginalRomsAsync();
        int representationId = await db.RomRepresentations.Select(row => row.Id).SingleAsync();
        var graph = (await new MediaFactReader(db).ReadAsync([representationId]))[representationId];
        return (1, LogicalInspectionFixture.Receipt(graph));
    }

    private static async Task StageCallerBootProtocolAsync(RomdDbContext db, (int RomId, RomRetainedMeasurement Retained) owner,
        RomDiscMediaReferenceRecord record, Guid inspectionId, CancellationToken ct)
    {
        var boot = new RomPs2BootLayoutFacts(record.LogicalBytes, 64, 16, 24, 200, 25, 123, 28, 896,
            @"cdrom0:\slus_203.12;1", @"cdrom0:\SLUS_203.12;1", "SLUS_203.12", 0x100000, 2,
            RomPs2BootLayoutFacts.ProfileVersion, "import-wakeup-inspector/v1");
        var identity = new RomLogicalIdentity(record.LogicalBytes, Sha256.Parse(new string('a', 64)), record.Sha1, record.Md5, record.Crc32);
        var observation = new RomLogicalInspection(inspectionId, identity, "import-wakeup-structure/v1", boot.InspectorBuild,
            new DateTimeOffset(SubMillisecondTicks, TimeSpan.Zero), new byte[32], layout: boot);
        (await new RomMediaObservationStore(db, new Romd.Persistence.Repositories.Reconciliation.MediaReconciliationRequests(db, TimeProvider.System))
            .StageAsync(owner.RomId, owner.Retained, observation, ct)).IsError.ShouldBeFalse();
    }

    private static Task<List<Guid>> InspectionIdsAsync(RomdDbContext db) => db.Database.SqlQueryRaw<Guid>(
        "SELECT \"Id\" AS \"Value\" FROM romd.\"LogicalInspections\"").ToListAsync();

    private static async Task AssertWaitingForTupleGuardAsync(PostgreSqlTestDatabase database, int backendPid, CancellationToken ct)
    {
        await using var observer = database.CreateContext();
        while (true)
        {
            bool waiting = await observer.Database.SqlQuery<bool>($"""
                SELECT EXISTS (SELECT 1 FROM pg_locks WHERE locktype = 'advisory'
                    AND pid = {backendPid} AND classid::bigint = 149 AND NOT granted) AS "Value"
                """).SingleAsync(ct);
            if (waiting)
                return;
            await Task.Delay(TimeSpan.FromMilliseconds(10), ct);
        }
    }

    private static async Task JoinCancelledAsync(Task task)
    {
        try
        { await task; }
        catch (OperationCanceledException) { }
    }

    private static RomDiscMediaReferenceRecord Record(Guid? id = null, uint crc = 42,
        DateTimeOffset? time = null, byte[]? raw = null, RomDiscReferenceMedia media = RomDiscReferenceMedia.Dvd5,
        long logicalBytes = 8L << 30)
    {
        raw ??= Body;
        return new(id ?? Identity, RomDiscReferenceSystem.PlayStation2, media, 1, logicalBytes,
            Sha1.Parse(new string('1', 40)), Md5.Parse(new string('2', 32)), Crc32.FromUInt32(crc),
            raw.LongLength, Sha256.FromSpan(SHA256.HashData(raw)), "https://example.test/reference/42",
            "synthetic-reference/v1", "caller-admitted-unit-fixture/v1", time ?? new DateTimeOffset(SubMillisecondTicks, TimeSpan.Zero));
    }

    private static RomDiscMediaReferenceRecord ChangedRecord(RomDiscMediaReferenceRecord record, string changed, byte[] raw) => new(
        record.Id, changed == "system" ? RomDiscReferenceSystem.PlayStation1 : record.System,
        changed == "media" ? RomDiscReferenceMedia.Cd : record.Media,
        changed == "file-count" ? record.FileCount + 1 : record.FileCount,
        changed == "logical-size" ? record.LogicalBytes + 2048 : record.LogicalBytes,
        changed == "sha1" ? Sha1.Parse(new string('3', 40)) : record.Sha1,
        changed == "md5" ? Md5.Parse(new string('4', 32)) : record.Md5,
        changed == "crc" ? Crc32.FromUInt32(43) : record.Crc32,
        raw.LongLength, Sha256.FromSpan(SHA256.HashData(raw)),
        changed == "source" ? "https://example.test/reference/43" : record.ReferenceSource,
        changed == "profile" ? "different-parser/v1" : record.ParserProfile,
        changed == "provenance" ? "different-admission/v1" : record.AcceptedSourceProvenance,
        changed == "timestamp" ? record.RetrievedAt.AddTicks(1) : record.RetrievedAt);

    private static RomdDbContext ClosedContext() => new(new DbContextOptionsBuilder<RomdDbContext>()
        .UseNpgsql("Host=localhost;Database=romd;Username=romd").Options);
    private static RomdDbContext Context(PostgreSqlTestDatabase database, CommandObserver commands) =>
        new(database.CreateOptions(options => options.AddInterceptors(commands)));

    private static async Task AssertCancellation(Task operation, CancellationToken expected)
    {
        OperationCanceledException? observed = null;
        try
        { await operation; }
        catch (OperationCanceledException failure) { observed = failure; }
        if (observed is null)
            throw new InvalidOperationException("Expected caller cancellation.");
        observed.CancellationToken.ShouldBe(expected);
    }

    private static Exception Failure(string kind) => kind switch
    {
        "npgsql-transient" => new NpgsqlException("synthetic connection fault", new IOException("synthetic transport fault")),
        "timeout" => new TimeoutException("synthetic database timeout"),
        "npgsql-nontransient" => new NpgsqlException("synthetic nontransient fault"),
        "constraint" => new PostgresException("synthetic constraint fault", "ERROR", "ERROR", PostgresErrorCodes.CheckViolation),
        "logic-timeout" => new InvalidOperationException("synthetic staging logic fault", new TimeoutException("nested diagnostic")),
        "wrapped-npgsql-nontransient" => new InvalidOperationException(
            "An exception has been raised that is likely due to a transient failure.", new NpgsqlException("synthetic nontransient fault")),
        _ => new InvalidOperationException("synthetic staging logic fault")
    };

    private static CommandObserver FailingCommand(string phase, Exception failure, Action? beforeFailure = null) => phase switch
    {
        "guard" => new() { GuardFailure = failure, BeforeGuard = beforeFailure },
        "insert" => new() { InsertFailure = failure, BeforeInsert = beforeFailure },
        "reload" => new() { ReloadFailure = failure, BeforeReload = beforeFailure },
        "subjects" => new() { SubjectsFailure = failure, BeforeSubjects = beforeFailure },
        _ => throw new ArgumentOutOfRangeException(nameof(phase))
    };

    private sealed class CorruptMaterializedBody : IMaterializationInterceptor
    {
        public int RowsMutated { get; private set; }
        public object InitializedInstance(MaterializationInterceptionData materializationData, object entity)
        {
            if (entity is RomDiscMediaReferenceEntity)
            {
                typeof(RomDiscMediaReferenceEntity).GetProperty("RawBody", BindingFlags.Instance | BindingFlags.NonPublic)
                    .ShouldNotBeNull().SetValue(entity, new byte[] { 1, 0, 254 });
                RowsMutated++;
            }
            return entity;
        }
    }

    private sealed class CommandObserver : DbCommandInterceptor
    {
        public int Guards { get; private set; }
        public int Writes { get; private set; }
        public int Reads { get; private set; }
        public int RoleReads { get; private set; }
        public Action? BeforeGuard { get; init; }
        public Action? BeforeInsert { get; init; }
        public Action? BeforeReload { get; init; }
        public Action? BeforeSubjects { get; init; }
        public Exception? GuardFailure { get; init; }
        public Exception? InsertFailure { get; init; }
        public Exception? ReloadFailure { get; init; }
        public Exception? SubjectsFailure { get; init; }
        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("pg_advisory_xact_lock", StringComparison.Ordinal))
            {
                Guards++;
                BeforeGuard?.Invoke();
                if (GuardFailure is not null)
                    throw GuardFailure;
                return ValueTask.FromResult(result);
            }
            if (!command.CommandText.Contains("RomDiscMediaReferences", StringComparison.Ordinal))
                return ValueTask.FromResult(result);
            Writes++;
            BeforeInsert?.Invoke();
            if (InsertFailure is not null)
                throw InsertFailure;
            return ValueTask.FromResult(result);
        }
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("FROM romd.\"Roms\"", StringComparison.Ordinal) && command.CommandText.Contains("ContentSize", StringComparison.Ordinal))
            {
                BeforeSubjects?.Invoke();
                if (SubjectsFailure is not null)
                    throw SubjectsFailure;
            }
            if (command.CommandText.Contains("SELECT current_user AS \"Value\"", StringComparison.Ordinal))
            {
                RoleReads++;
                return ValueTask.FromResult(result);
            }
            if (!command.CommandText.Contains("RomDiscMediaReferences", StringComparison.Ordinal))
                return ValueTask.FromResult(result);
            Reads++;
            BeforeReload?.Invoke();
            if (ReloadFailure is not null)
                throw ReloadFailure;
            return ValueTask.FromResult(result);
        }
    }
}
