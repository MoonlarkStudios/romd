using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Romd.Persistence.Migrations;

/// <summary>Adds optional installation-owned hardware facts without changing system identities.</summary>
[DbContext(typeof(RomdDbContext))]
[Migration("20261118000000_SystemHardwareMetadata")]
public sealed class SystemHardwareMetadata : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>("HardwareCategory", "Platforms", type: "character varying(20)",
            maxLength: 20, schema: PostgreSqlConfiguration.SchemaName, nullable: true);
        migrationBuilder.AddColumn<int>("HardwareGeneration", "Platforms", type: "integer",
            schema: PostgreSqlConfiguration.SchemaName, nullable: true);
        migrationBuilder.AddColumn<int>("HardwareReleaseYear", "Platforms", type: "integer",
            schema: PostgreSqlConfiguration.SchemaName, nullable: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn("HardwareCategory", "Platforms", PostgreSqlConfiguration.SchemaName);
        migrationBuilder.DropColumn("HardwareGeneration", "Platforms", PostgreSqlConfiguration.SchemaName);
        migrationBuilder.DropColumn("HardwareReleaseYear", "Platforms", PostgreSqlConfiguration.SchemaName);
    }
}
