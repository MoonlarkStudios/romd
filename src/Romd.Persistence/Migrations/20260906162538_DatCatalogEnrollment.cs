using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Romd.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class DatCatalogEnrollment : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<int>(
                name: "DatSourceId",
                schema: "romd",
                table: "DatSubscriptions",
                type: "integer",
                nullable: true,
                oldClrType: typeof(int),
                oldType: "integer");

            migrationBuilder.AddColumn<string>(
                name: "ExpectedName",
                schema: "romd",
                table: "DatSubscriptions",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "PlatformId",
                schema: "romd",
                table: "DatSubscriptions",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SystemId",
                schema: "romd",
                table: "DatSubscriptions",
                type: "character varying(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_DatSubscriptions_CatalogId",
                schema: "romd",
                table: "DatSubscriptions",
                column: "CatalogId",
                unique: true,
                filter: "\"DatSourceId\" IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DO $$ BEGIN
                    IF EXISTS (SELECT 1 FROM romd."DatSubscriptions" WHERE "DatSourceId" IS NULL) THEN
                        RAISE EXCEPTION 'Cannot downgrade while pending catalog subscriptions exist; complete or remove them first.';
                    END IF;
                END $$;
                """);
            migrationBuilder.DropIndex(
                name: "IX_DatSubscriptions_CatalogId",
                schema: "romd",
                table: "DatSubscriptions");

            migrationBuilder.DropColumn(
                name: "ExpectedName",
                schema: "romd",
                table: "DatSubscriptions");

            migrationBuilder.DropColumn(
                name: "PlatformId",
                schema: "romd",
                table: "DatSubscriptions");

            migrationBuilder.DropColumn(
                name: "SystemId",
                schema: "romd",
                table: "DatSubscriptions");

            migrationBuilder.AlterColumn<int>(
                name: "DatSourceId",
                schema: "romd",
                table: "DatSubscriptions",
                type: "integer",
                nullable: false,
                oldClrType: typeof(int),
                oldType: "integer",
                oldNullable: true);
        }
    }
}
