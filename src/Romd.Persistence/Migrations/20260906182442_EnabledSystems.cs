using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Romd.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class EnabledSystems : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsEnabled",
                schema: "romd",
                table: "Platforms",
                type: "boolean",
                nullable: true);

            // Existing user data and deliberate setup stay visible. Null is an
            // untouched registry definition; false is an explicit local disable.
            // Legacy seeding used the reserved system user (…0001), not Guid.Empty.
            migrationBuilder.Sql("""
                UPDATE romd."Platforms" p SET "IsEnabled" = TRUE
                WHERE EXISTS (SELECT 1 FROM romd."DatFiles" d WHERE d."PlatformId" = p."Id")
                   OR EXISTS (SELECT 1 FROM romd."Titles" t WHERE t."PlatformId" = p."Id")
                   OR EXISTS (SELECT 1 FROM romd."DatSubscriptions" s WHERE s."PlatformId" = p."Id")
                   OR EXISTS (SELECT 1 FROM romd."Jobs" j WHERE j."PlatformId" = p."Id")
                   OR p."CreatedByUserId" NOT IN (
                       '00000000-0000-0000-0000-000000000000'::uuid,
                       '00000000-0000-0000-0000-000000000001'::uuid);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "IsEnabled",
                schema: "romd",
                table: "Platforms");
        }
    }
}
