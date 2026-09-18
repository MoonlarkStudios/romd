using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Romd.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AtomicBackendMutations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "Revision",
                schema: "romd",
                table: "Titles",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "MaterializationRevision",
                schema: "romd",
                table: "Libraries",
                type: "uuid",
                nullable: false,
                defaultValueSql: "gen_random_uuid()");

            migrationBuilder.CreateTable(
                name: "MetadataRematerializationRequests",
                schema: "romd",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TitleId = table.Column<int>(type: "integer", nullable: true),
                    PlatformId = table.Column<int>(type: "integer", nullable: true),
                    CreatedAtUtc = table.Column<long>(type: "bigint", nullable: false),
                    AvailableAtUtc = table.Column<long>(type: "bigint", nullable: false),
                    Attempts = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MetadataRematerializationRequests", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MetadataRematerializationRequests_AvailableAtUtc_Id",
                schema: "romd",
                table: "MetadataRematerializationRequests",
                columns: new[] { "AvailableAtUtc", "Id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MetadataRematerializationRequests",
                schema: "romd");

            migrationBuilder.DropColumn(
                name: "Revision",
                schema: "romd",
                table: "Titles");

            migrationBuilder.DropColumn(
                name: "MaterializationRevision",
                schema: "romd",
                table: "Libraries");
        }
    }
}
