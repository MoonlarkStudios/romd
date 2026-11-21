using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Romd.Persistence.Migrations;

[DbContext(typeof(RomdDbContext))]
[Migration("20261121030000_ImportOutcomeReasons")]
public sealed class ImportOutcomeReasons : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) =>
        migrationBuilder.AddColumn<int>("Reason", "JobItems", type: "integer",
            schema: PostgreSqlConfiguration.SchemaName, nullable: true);

    protected override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.DropColumn("Reason", "JobItems", PostgreSqlConfiguration.SchemaName);
}
