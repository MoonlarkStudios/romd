using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Romd.Persistence.Migrations;

/// <summary>Indexes lease recovery for unfinished jobs that own an execution fence.</summary>
[DbContext(typeof(RomdDbContext))]
[Migration("20261117000000_JobDispatchExpiryIndex")]
public sealed class JobDispatchExpiryIndex : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) =>
        migrationBuilder.CreateIndex(
            name: "IX_Jobs_ExecutionLeaseRecovery",
            schema: PostgreSqlConfiguration.SchemaName,
            table: "Jobs",
            columns: ["ExecutionLeaseExpiresAtUtc", "Id"],
            filter: "\"CompletedAt\" IS NULL AND \"ExecutionFenceToken\" IS NOT NULL");

    protected override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.DropIndex(
            name: "IX_Jobs_ExecutionLeaseRecovery",
            schema: PostgreSqlConfiguration.SchemaName,
            table: "Jobs");
}
