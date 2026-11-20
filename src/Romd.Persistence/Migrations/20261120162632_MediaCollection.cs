using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Romd.Persistence.Migrations;

/// <summary>
///     Adds release placement, region, language, packaging preference and provider asset identity to title media.
///     Existing rows stay title-level, unpreferred and keep their types; no evidence is backfilled.
/// </summary>
[DbContext(typeof(RomdDbContext))]
[Migration("20261120162632_MediaCollection")]
public sealed class MediaCollection : Migration
{
    private const string Schema = PostgreSqlConfiguration.SchemaName;

    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<int>(name: "CatalogReleaseId", schema: Schema, table: "TitleMedia",
            type: "integer", nullable: true);
        migrationBuilder.AddColumn<bool>(name: "IsPreferred", schema: Schema, table: "TitleMedia",
            type: "boolean", nullable: false, defaultValue: false);
        migrationBuilder.AddColumn<string>(name: "Language", schema: Schema, table: "TitleMedia",
            type: "character varying(100)", maxLength: 100, nullable: true);
        migrationBuilder.AddColumn<string>(name: "ProviderAssetId", schema: Schema, table: "TitleMedia",
            type: "character varying(200)", maxLength: 200, nullable: true);
        migrationBuilder.AddColumn<string>(name: "Region", schema: Schema, table: "TitleMedia",
            type: "character varying(100)", maxLength: 100, nullable: true);

        migrationBuilder.AddCheckConstraint(name: "CK_TitleMedia_PreferredPackaging", schema: Schema,
            table: "TitleMedia",
            sql: "NOT \"IsPreferred\" OR \"Type\" IN ('BoxFront', 'BoxBack', 'BoxSpine', 'BoxFull', 'CartridgeFront', 'CartridgeBack', 'DiscLabel')");

        migrationBuilder.CreateIndex(name: "IX_TitleMedia_CatalogReleaseId", schema: Schema, table: "TitleMedia",
            column: "CatalogReleaseId");
        migrationBuilder.CreateIndex(name: "IX_TitleMedia_PreferredPerRelease", schema: Schema, table: "TitleMedia",
            columns: ["TitleId", "Type", "CatalogReleaseId"], unique: true,
            filter: "\"IsPreferred\" AND \"CatalogReleaseId\" IS NOT NULL");

        // A release removed by catalog projection leaves its media in the library at title level.
        migrationBuilder.AddForeignKey(name: "FK_TitleMedia_CatalogReleases_CatalogReleaseId", schema: Schema,
            table: "TitleMedia", column: "CatalogReleaseId", principalSchema: Schema,
            principalTable: "CatalogReleases", principalColumn: "Id", onDelete: ReferentialAction.SetNull);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropForeignKey(name: "FK_TitleMedia_CatalogReleases_CatalogReleaseId", schema: Schema,
            table: "TitleMedia");
        migrationBuilder.DropIndex(name: "IX_TitleMedia_CatalogReleaseId", schema: Schema, table: "TitleMedia");
        migrationBuilder.DropIndex(name: "IX_TitleMedia_PreferredPerRelease", schema: Schema, table: "TitleMedia");
        migrationBuilder.DropCheckConstraint(name: "CK_TitleMedia_PreferredPackaging", schema: Schema,
            table: "TitleMedia");
        migrationBuilder.DropColumn(name: "CatalogReleaseId", schema: Schema, table: "TitleMedia");
        migrationBuilder.DropColumn(name: "IsPreferred", schema: Schema, table: "TitleMedia");
        migrationBuilder.DropColumn(name: "Language", schema: Schema, table: "TitleMedia");
        migrationBuilder.DropColumn(name: "ProviderAssetId", schema: Schema, table: "TitleMedia");
        migrationBuilder.DropColumn(name: "Region", schema: Schema, table: "TitleMedia");
    }
}
