using Microsoft.EntityFrameworkCore;
using Npgsql;
using Romd.Domain.Jobs;
using Romd.Domain.Source.Rom;
using Romd.Persistence.Entities;
using Romd.PostgreSql.TestSupport;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Persistence;

public sealed class ExportFormatSchemaTests : IDisposable
{
    private readonly PostgreSqlTestDatabase _database = PostgreSqlTestDatabase.Create();

    [Theory]
    [InlineData("RVZ")]
    [InlineData("iso")]
    [InlineData("")]
    public async Task FreshBaseline_UnknownSelection_IsRejectedByDatabase(string selection)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var ct = timeout.Token;
        await using var db = _database.CreateContext();
        var job = ExportJob.CreateAllCatalog(createdAt: DateTimeOffset.UtcNow);
        db.Jobs.Add(ExportJobEntity.FromDomain(job));
        await db.SaveChangesAsync(ct);

        var error = await Should.ThrowAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE romd."Jobs" SET "ExportFormat" = {selection} WHERE "Id" = {job.Id}
            """, ct));

        error.SqlState.ShouldBe(PostgresErrorCodes.CheckViolation);
        error.ConstraintName.ShouldBe("CK_Jobs_ExportFormat");
        await using var observer = _database.CreateContext();
        (await observer.Jobs.OfType<ExportJobEntity>().SingleAsync(ct)).ToDomain().Format
            .ShouldBe(RomDeliveryFormat.Original);
    }

    [Theory]
    [InlineData(RomDeliveryFormat.Original)]
    [InlineData(RomDeliveryFormat.Rvz)]
    [InlineData(RomDeliveryFormat.Chd)]
    public async Task FreshBaseline_SupportedFormat_PreservesAuthorizedJobContext(RomDeliveryFormat format)
    {
        await using var db = _database.CreateContext();
        var job = ExportJob.CreateAllCatalog(createdAt: DateTimeOffset.UtcNow, format: format);
        db.Jobs.Add(ExportJobEntity.FromDomain(job));
        await db.SaveChangesAsync();
        var retained = (await db.Jobs.OfType<ExportJobEntity>().SingleAsync()).ToDomain();
        retained.Format.ShouldBe(format);
        retained.ScopeKind.ShouldBe(job.ScopeKind);
        retained.CorrelationId.ShouldBe(job.CorrelationId);
        retained.CreatedByUserId.ShouldBe(job.CreatedByUserId);
    }

    public void Dispose() => _database.Dispose();
}
