using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Romd.Persistence.Migrations;

[DbContext(typeof(RomdDbContext))]
[Migration("20261124020000_TitleMergeRedirects")]
public sealed class TitleMergeRedirects : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "TitleMergeRedirects",
            schema: "romd",
            columns: table => new
            {
                SourceTitleId = table.Column<int>(type: "integer", nullable: false),
                TargetTitleId = table.Column<int>(type: "integer", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_TitleMergeRedirects", x => x.SourceTitleId);
                table.CheckConstraint("CK_TitleMergeRedirects_DistinctTitles", "\"SourceTitleId\" <> \"TargetTitleId\"");
                table.ForeignKey(
                    name: "FK_TitleMergeRedirects_Titles_TargetTitleId",
                    column: x => x.TargetTitleId,
                    principalSchema: "romd",
                    principalTable: "Titles",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            name: "IX_TitleMergeRedirects_TargetTitleId",
            schema: "romd",
            table: "TitleMergeRedirects",
            column: "TargetTitleId");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "TitleMergeRedirects",
            schema: "romd");
    }
}
