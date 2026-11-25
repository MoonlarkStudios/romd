using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Romd.Persistence.Migrations;

[DbContext(typeof(RomdDbContext))]
[Migration("20261123000000_DatParentDeclarations")]
public sealed class DatParentDeclarations : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>("DeclaredId", "DatGames", "text", schema: "romd", nullable: true);
        migrationBuilder.AddColumn<string>("CloneOfId", "DatGames", "text", schema: "romd", nullable: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn("CloneOfId", "DatGames", "romd");
        migrationBuilder.DropColumn("DeclaredId", "DatGames", "romd");
    }
}
