using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Romd.Persistence.Migrations;

[DbContext(typeof(RomdDbContext))]
[Migration("20261121200000_LibraryPublicationOutcome")]
public sealed class LibraryPublicationOutcome : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<Guid>("ReviewedRevision", "Jobs", "uuid", schema: "romd", nullable: true);
        migrationBuilder.AddColumn<long>("PublishedGeneration", "Jobs", "bigint", schema: "romd", nullable: true);
        migrationBuilder.AddColumn<string>("PublicationOutcome", "Jobs", "text", schema: "romd", nullable: true);
        migrationBuilder.CreateIndex("IX_MaterializationJobs_LibraryHistory", "Jobs",
            ["LibraryId", "CreatedAt", "Id"], schema: "romd", filter: "\"JobType\" = 'materialization'");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex("IX_MaterializationJobs_LibraryHistory", "Jobs", "romd");
        migrationBuilder.DropColumn("ReviewedRevision", "Jobs", "romd");
        migrationBuilder.DropColumn("PublishedGeneration", "Jobs", "romd");
        migrationBuilder.DropColumn("PublicationOutcome", "Jobs", "romd");
    }
}
