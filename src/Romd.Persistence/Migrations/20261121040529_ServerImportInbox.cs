using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Romd.Persistence.Migrations;

[DbContext(typeof(RomdDbContext))]
[Migration("20261121040529_ServerImportInbox")]
public sealed class ServerImportInbox : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "ServerImportActivity",
            schema: "romd",
            columns: table => new
            {
                DirectoryKey = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                CheckedAt = table.Column<long>(type: "bigint", nullable: false),
                Outcome = table.Column<int>(type: "integer", nullable: false),
                WaitingFiles = table.Column<int>(type: "integer", nullable: false),
                Message = table.Column<string>(type: "text", nullable: true),
                JobId = table.Column<Guid>(type: "uuid", nullable: true),
                LastQueuedFiles = table.Column<int>(type: "integer", nullable: false),
                SubmittedFilesJson = table.Column<string>(type: "jsonb", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_ServerImportActivity", x => x.DirectoryKey);
            });

        migrationBuilder.CreateTable(
            name: "ServerImportSelections",
            schema: "romd",
            columns: table => new
            {
                JobId = table.Column<Guid>(type: "uuid", nullable: false),
                FilesJson = table.Column<string>(type: "jsonb", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_ServerImportSelections", x => x.JobId);
                table.ForeignKey(
                    name: "FK_ServerImportSelections_Jobs_JobId",
                    column: x => x.JobId,
                    principalSchema: "romd",
                    principalTable: "Jobs",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "ServerImportActivity",
            schema: "romd");

        migrationBuilder.DropTable(
            name: "ServerImportSelections",
            schema: "romd");
    }
}
