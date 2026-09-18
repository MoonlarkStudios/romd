using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Romd.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class SharedReferenceData : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ReferenceDataState",
                schema: "romd",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false),
                    CanSeedFresh = table.Column<bool>(type: "boolean", nullable: false),
                    InstalledDocument = table.Column<string>(type: "text", nullable: true),
                    InstalledHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    InstalledPublicationVersion = table.Column<long>(type: "bigint", nullable: false),
                    BindingsJson = table.Column<string>(type: "text", nullable: false),
                    InitializedAt = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReferenceDataState", x => x.Id);
                    table.CheckConstraint("CK_ReferenceDataState_Singleton", "\"Id\" = 1");
                });

            // Capture prior initialization before the provisioner publishes its
            // new schema marker. Empty tables on an existing install are choices.
            migrationBuilder.Sql("""
                INSERT INTO romd."ReferenceDataState"
                    ("Id", "CanSeedFresh", "InstalledPublicationVersion", "BindingsJson")
                SELECT 1,
                    to_regclass('romd.romd_schema') IS NULL
                    AND NOT EXISTS (SELECT 1 FROM romd."Platforms")
                    AND NOT EXISTS (SELECT 1 FROM romd."Regions")
                    AND NOT EXISTS (SELECT 1 FROM romd."GameLanguages"),
                    0, '{}';
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ReferenceDataState",
                schema: "romd");
        }
    }
}
