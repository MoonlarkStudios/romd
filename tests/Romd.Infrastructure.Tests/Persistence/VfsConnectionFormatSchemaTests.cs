using Microsoft.EntityFrameworkCore;
using Npgsql;
using Romd.Persistence.Entities;
using Romd.Persistence.Identity;
using Romd.PostgreSql.TestSupport;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Persistence;

public sealed class VfsConnectionFormatSchemaTests : IDisposable
{
    private readonly PostgreSqlTestDatabase _database = PostgreSqlTestDatabase.Create();

    [Theory]
    [InlineData("STORED")]
    [InlineData("rvz")]
    [InlineData("")]
    public async Task FreshBaseline_UnknownFormat_IsRejectedByDatabase(string format)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var ct = timeout.Token;
        await using var db = _database.CreateContext();
        var connection = await SeedAsync(db, ct);
        string facts = await FactsAsync(db, connection.Id, ct);
        var error = await Should.ThrowAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE romd."VfsConnections" SET "Format" = {format} WHERE "Id" = {connection.Id}
            """, ct));
        error.SqlState.ShouldBe(PostgresErrorCodes.CheckViolation);
        error.ConstraintName.ShouldBe("CK_VfsConnections_Format");
        await using var observer = _database.CreateContext();
        (await FormatAsync(observer, connection.Id, ct)).ShouldBe("stored");
        (await FactsAsync(observer, connection.Id, ct)).ShouldBe(facts);
    }

    [Fact]
    public async Task FreshBaseline_NullFormat_IsRejectedByDatabase()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var ct = timeout.Token;
        await using var db = _database.CreateContext();
        var connection = await SeedAsync(db, ct);
        var error = await Should.ThrowAsync<PostgresException>(() => db.Database.ExecuteSqlRawAsync("""
            UPDATE romd."VfsConnections" SET "Format" = NULL
            """, ct));
        error.SqlState.ShouldBe(PostgresErrorCodes.NotNullViolation);
        await using var observer = _database.CreateContext();
        (await FormatAsync(observer, connection.Id, ct)).ShouldBe("stored");
    }

    [Theory]
    [InlineData("stored")]
    [InlineData("original")]
    public async Task FreshBaseline_SupportedFormat_PreservesCredentialsAndAccountBinding(string format)
    {
        await using var db = _database.CreateContext();
        var connection = await SeedAsync(db, default);
        string before = await FactsAsync(db, connection.Id, default);
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE romd."VfsConnections" SET "Format" = {format} WHERE "Id" = {connection.Id}
            """);
        (await FormatAsync(db, connection.Id, default)).ShouldBe(format);
        (await FactsAsync(db, connection.Id, default)).ShouldBe(before);
    }

    private static async Task<VfsConnectionEntity> SeedAsync(Romd.Persistence.RomdDbContext db, CancellationToken ct)
    {
        var user = new RomdUser { Id = Guid.NewGuid(), UserName = "fixture", SecurityStamp = "fixture-stamp" };
        db.Users.Add(user);
        var connection = new VfsConnectionEntity
        {
            UserId = user.Id,
            Name = "Fixture Deck",
            PasswordVerifier = new byte[32],
            AccountStamp = user.SecurityStamp,
            CreatedAt = DateTimeOffset.UtcNow,
            CredentialUpdatedAt = DateTimeOffset.UtcNow
        };
        db.VfsConnections.Add(connection);
        await db.SaveChangesAsync(ct);
        return connection;
    }

    private static Task<string> FormatAsync(Romd.Persistence.RomdDbContext db, Guid id, CancellationToken ct) =>
        db.Database.SqlQuery<string>($"""
            SELECT "Format" AS "Value" FROM romd."VfsConnections" WHERE "Id" = {id}
            """).SingleAsync(ct);

    private static Task<string> FactsAsync(Romd.Persistence.RomdDbContext db, Guid id, CancellationToken ct) =>
        db.Database.SqlQuery<string>($"""
            SELECT (to_jsonb(c) - 'Format')::text AS "Value" FROM romd."VfsConnections" c WHERE "Id" = {id}
            """).SingleAsync(ct);

    public void Dispose() => _database.Dispose();
}
