using Romd.Domain.Source.Rom;
using System.Data;
using System.Data.Common;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;
using Romd.Admin.Application.Source.Rom;
using Romd.Domain.Hashing;
using Romd.Persistence;
using Romd.Persistence.Entities;
using Romd.Persistence.Repositories;
using Romd.PostgreSql.TestSupport;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Persistence;

/// <remarks>Database cases require the primary's manual entity registration and migration.
/// They use the existing migrated PostgreSQL fixture, not a substitute LINQ provider.</remarks>
public sealed class RomDiscMediaReferenceStoreTests
{
    private static readonly RomDiscMediaReferenceQuery Query = new(8L << 30,
        Sha1.Parse(new string('1', 40)), Md5.Parse(new string('2', 32)), Crc32.FromUInt32(uint.MaxValue));

    [Fact]
    public async Task FindAsync_AllFourFieldsAndNoTracking_RetainsMatchingConflictsAndRawCustody()
    {
        await using var database = PostgreSqlTestDatabase.Create();
        await using var db = database.CreateContext();
        var ordinary = NewRow(1);
        var conflictingSystem = NewRow(2, system: RomDiscReferenceSystem.PlayStation1, media: RomDiscReferenceMedia.Cd);
        var conflictingMedia = NewRow(3, media: RomDiscReferenceMedia.Dvd9);
        db.AddRange(ordinary, conflictingSystem, conflictingMedia,
            NewRow(4, logicalBytes: Query.LogicalBytes + 2048),
            NewRow(5, sha1: Sha1.Parse(new string('3', 40))),
            NewRow(6, md5: Md5.Parse(new string('4', 32))),
            NewRow(7, crc: uint.MaxValue - 1));
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        db.ChangeTracker.QueryTrackingBehavior = QueryTrackingBehavior.TrackAll;
        var tracked = await db.Set<RomDiscMediaReferenceEntity>().AsTracking().SingleAsync(row => row.Id == ordinary.Id);
        db.Entry(tracked).Property(row => row.ReferenceSource).CurrentValue = "unsaved-local-alias";

        var result = await new RomDiscMediaReferenceStore(db).FindAsync(Query, 129);

        result.IsError.ShouldBeFalse();
        result.Value.ShouldBe(new[] { ordinary.ToDomain(), conflictingSystem.ToDomain(), conflictingMedia.ToDomain() });
        result.Value.ShouldAllBe(record => record.Matches(Query) && record.ReferenceSource != "unsaved-local-alias");
        db.ChangeTracker.Entries<RomDiscMediaReferenceEntity>().ShouldHaveSingleItem().Entity.ShouldBeSameAs(tracked);
        var retained = await db.Set<RomDiscMediaReferenceEntity>().AsNoTracking().SingleAsync(row => row.Id == ordinary.Id);
        retained.GetRawBodySnapshot().ShouldBe(ordinary.GetRawBodySnapshot());
        retained.ToDomain().RetrievedAt.UtcTicks.ShouldBe(ordinary.RetrievedAtUtcTicks);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(129)]
    public async Task FindAsync_BoundedOrderedResult_PreservesOverflowSentinel(int maximumResults)
    {
        await using var database = PostgreSqlTestDatabase.Create();
        await using var db = database.CreateContext();
        db.AddRange(Enumerable.Range(1, 130).Reverse().Select(id => NewRow(id)));
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var result = await new RomDiscMediaReferenceStore(db).FindAsync(Query, maximumResults);

        result.IsError.ShouldBeFalse();
        result.Value.Select(record => record.Id).ShouldBe(Enumerable.Range(1, maximumResults).Select(Id));
        db.ChangeTracker.Entries().ShouldBeEmpty();
    }

    [Fact]
    public async Task FindAsync_AbsentTuple_ReturnsSuccessfulEmptyList()
    {
        await using var database = PostgreSqlTestDatabase.Create();
        await using var db = database.CreateContext();
        var result = await new RomDiscMediaReferenceStore(db).FindAsync(Query, 129);
        result.IsError.ShouldBeFalse();
        result.Value.ShouldBeEmpty();
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(130)]
    [InlineData(int.MaxValue)]
    public async Task FindAsync_InvalidCapacity_ThrowsBeforeDatabaseUse(int capacity)
    {
        await using var db = ClosedContext();
        await Should.ThrowAsync<ArgumentOutOfRangeException>(() => new RomDiscMediaReferenceStore(db).FindAsync(Query, capacity));
        db.Database.GetDbConnection().State.ShouldBe(ConnectionState.Closed);
    }

    [Fact]
    public async Task FindAsync_PreCancelled_PropagatesCallerTokenBeforeDatabaseUse()
    {
        await using var db = ClosedContext();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        try
        {
            await new RomDiscMediaReferenceStore(db).FindAsync(Query, 129, cancellation.Token);
            throw new InvalidOperationException("A cancelled reference lookup must throw.");
        }
        catch (OperationCanceledException failure) { failure.CancellationToken.ShouldBe(cancellation.Token); }
        db.Database.GetDbConnection().State.ShouldBe(ConnectionState.Closed);
    }

    [Theory]
    [InlineData("npgsql-transient")]
    [InlineData("timeout")]
    public async Task FindAsync_TransientReaderFailure_ReturnsUnavailableRatherThanAbsence(string kind)
    {
        await using var database = PostgreSqlTestDatabase.Create();
        var injected = Failure(kind);
        var interceptor = new ReaderFailure(injected);
        await using var db = new RomdDbContext(database.CreateOptions(options => options.AddInterceptors(interceptor)));
        var result = await new RomDiscMediaReferenceStore(db).FindAsync(Query, 129);
        interceptor.Calls.ShouldBe(1);
        result.IsError.ShouldBeTrue();
        result.Errors.ShouldHaveSingleItem().Code.ShouldBe(RomDiscMediaReferenceErrors.UnavailableCode);
        db.ChangeTracker.Entries().ShouldBeEmpty();
    }

    [Theory]
    [InlineData("npgsql-nontransient")]
    [InlineData("constraint")]
    [InlineData("logic")]
    [InlineData("logic-timeout")]
    [InlineData("wrapped-npgsql-nontransient")]
    public async Task FindAsync_NontransientReaderFailure_PropagatesOriginalFailure(string kind)
    {
        await using var database = PostgreSqlTestDatabase.Create();
        var injected = Failure(kind);
        var interceptor = new ReaderFailure(injected);
        await using var db = new RomdDbContext(database.CreateOptions(options => options.AddInterceptors(interceptor)));
        Exception? observed = null;
        try
        { await new RomDiscMediaReferenceStore(db).FindAsync(Query, 129); }
        catch (Exception error) { observed = error; }
        observed.ShouldBeSameAs(injected);
        interceptor.Calls.ShouldBe(1);
    }

    [Theory]
    [InlineData("npgsql-transient")]
    [InlineData("timeout")]
    public async Task FindAsync_CancellationDuringTransientFailure_PropagatesCallerToken(string kind)
    {
        await using var database = PostgreSqlTestDatabase.Create();
        using var cancellation = new CancellationTokenSource();
        var interceptor = new ReaderFailure(Failure(kind), cancellation.Cancel);
        await using var db = new RomdDbContext(database.CreateOptions(options => options.AddInterceptors(interceptor)));
        try
        {
            await new RomDiscMediaReferenceStore(db).FindAsync(Query, 129, cancellation.Token);
            throw new InvalidOperationException("Cancellation must take precedence over a transient reference lookup failure.");
        }
        catch (OperationCanceledException failure) { failure.CancellationToken.ShouldBe(cancellation.Token); }
        interceptor.Calls.ShouldBe(1);
    }

    [Fact]
    public async Task FindAsync_CancellationFromReader_PropagatesOriginalCancellation()
    {
        await using var database = PostgreSqlTestDatabase.Create();
        using var cancellation = new CancellationTokenSource();
        var injected = new OperationCanceledException(cancellation.Token);
        var interceptor = new ReaderFailure(injected, cancellation.Cancel);
        await using var db = new RomdDbContext(database.CreateOptions(options => options.AddInterceptors(interceptor)));
        Exception? observed = null;
        try
        { await new RomDiscMediaReferenceStore(db).FindAsync(Query, 129, cancellation.Token); }
        catch (Exception error) { observed = error; }
        observed.ShouldBeSameAs(injected);
        interceptor.Calls.ShouldBe(1);
    }

    private static Guid Id(int value) => Guid.Parse($"00000000-0000-0000-0000-{value:x12}");

    private static RomDiscMediaReferenceEntity NewRow(int id, long? logicalBytes = null, Sha1? sha1 = null,
        Md5? md5 = null, uint? crc = null, RomDiscReferenceSystem system = RomDiscReferenceSystem.PlayStation2,
        RomDiscReferenceMedia media = RomDiscReferenceMedia.Dvd5)
    {
        byte[] raw = [checked((byte)id), 0, 255];
        var record = new RomDiscMediaReferenceRecord(Id(id), system, media, 1, logicalBytes ?? Query.LogicalBytes,
            sha1 ?? Query.Sha1, md5 ?? Query.Md5, Crc32.FromUInt32(crc ?? Query.Crc32.ToUInt32()), raw.LongLength,
            Sha256.FromSpan(SHA256.HashData(raw)), $"https://example.test/reference/{id}", "synthetic-reference/v1",
            "caller-admitted-local-fixture", DateTimeOffset.UnixEpoch.AddTicks(1234 + id));
        return RomDiscMediaReferenceEntity.FromDomain(record, raw);
    }

    private static RomdDbContext ClosedContext() => new(new DbContextOptionsBuilder<RomdDbContext>()
        .UseNpgsql("Host=localhost;Database=romd;Username=romd").Options);

    private static Exception Failure(string kind) => kind switch
    {
        "npgsql-transient" => new NpgsqlException("synthetic connection fault", new IOException("synthetic transport fault")),
        "timeout" => new TimeoutException("synthetic database timeout"),
        "npgsql-nontransient" => new NpgsqlException("synthetic nontransient fault"),
        "constraint" => new PostgresException("synthetic constraint fault", "ERROR", "ERROR", PostgresErrorCodes.CheckViolation),
        "logic-timeout" => new InvalidOperationException("synthetic query logic fault", new TimeoutException("nested diagnostic")),
        "wrapped-npgsql-nontransient" => new InvalidOperationException(
            "An exception has been raised that is likely due to a transient failure.", new NpgsqlException("synthetic nontransient fault")),
        _ => new InvalidOperationException("synthetic query logic fault")
    };

    private sealed class ReaderFailure(Exception failure, Action? beforeFailure = null) : DbCommandInterceptor
    {
        public int Calls { get; private set; }
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            Calls++;
            beforeFailure?.Invoke();
            throw failure;
        }
    }
}
