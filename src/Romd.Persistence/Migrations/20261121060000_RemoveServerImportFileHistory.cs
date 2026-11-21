using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Romd.Persistence.Migrations;

[DbContext(typeof(RomdDbContext))]
[Migration("20261121060000_RemoveServerImportFileHistory")]
public sealed class RemoveServerImportFileHistory : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) =>
        migrationBuilder.DropColumn(
            name: "SubmittedFilesJson",
            schema: "romd",
            table: "ServerImportActivity");

    protected override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.AddColumn<string>(
            name: "SubmittedFilesJson",
            schema: "romd",
            table: "ServerImportActivity",
            type: "jsonb",
            nullable: false,
            defaultValueSql: "'[]'::jsonb");
}
