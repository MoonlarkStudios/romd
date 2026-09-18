using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Romd.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class CinematicBackdropArtwork : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_Jobs_ArtworkImportIdentity",
                schema: "romd",
                table: "Jobs");

            migrationBuilder.DropCheckConstraint(
                name: "CK_ArtworkSelections_Role",
                schema: "romd",
                table: "ArtworkSelections");

            migrationBuilder.DropCheckConstraint(
                name: "CK_ArtworkPreferences_Role",
                schema: "romd",
                table: "ArtworkPreferences");

            migrationBuilder.DropCheckConstraint(
                name: "CK_ArtworkAssets_Role",
                schema: "romd",
                table: "ArtworkAssets");

            migrationBuilder.AddColumn<bool>(
                name: "FillBackdrops",
                schema: "romd",
                table: "ArtworkEnrichmentSettings",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "ReviewBackdrops",
                schema: "romd",
                table: "ArtworkEnrichmentSettings",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.UpdateData(
                schema: "romd",
                table: "ArtworkEnrichmentSettings",
                keyColumn: "Id",
                keyValue: 1,
                columns: new[] { "FillBackdrops", "ReviewBackdrops" },
                values: new object[] { true, true });

            migrationBuilder.AddCheckConstraint(
                name: "CK_Jobs_ArtworkImportIdentity",
                schema: "romd",
                table: "Jobs",
                sql: "\"JobType\" <> 'artwork-import' OR (\"ArtworkTitleId\" IS NOT NULL AND \"ArtworkTitleId\" > 0 AND \"ArtworkRole\" IS NOT NULL AND \"ArtworkRole\" IN ('Poster', 'Hero', 'Logo', 'Backdrop') AND \"ArtworkSelectionRevision\" IS NOT NULL AND \"ArtworkSelectionRevision\" > 0 AND \"ArtworkProviderId\" IS NOT NULL AND length(btrim(\"ArtworkProviderId\")) > 0 AND \"ArtworkProviderGameId\" IS NOT NULL AND length(btrim(\"ArtworkProviderGameId\")) > 0 AND \"ArtworkProviderAssetId\" IS NOT NULL AND length(btrim(\"ArtworkProviderAssetId\")) > 0 AND \"ArtworkTrustedAssetUrl\" IS NOT NULL AND length(btrim(\"ArtworkTrustedAssetUrl\")) > 0 AND \"ArtworkWasSuperseded\" IS NOT NULL AND \"Phase\" IN ('Pending', 'Importing', 'Completed', 'Failed', 'Cancelled'))");

            migrationBuilder.AddCheckConstraint(
                name: "CK_ArtworkSelections_Role",
                schema: "romd",
                table: "ArtworkSelections",
                sql: "\"Role\" IN ('Poster', 'Hero', 'Logo', 'Backdrop')");

            migrationBuilder.AddCheckConstraint(
                name: "CK_ArtworkPreferences_Role",
                schema: "romd",
                table: "ArtworkPreferences",
                sql: "\"Role\" IN ('Poster', 'Hero', 'Logo', 'Backdrop')");

            migrationBuilder.AddCheckConstraint(
                name: "CK_ArtworkAssets_Role",
                schema: "romd",
                table: "ArtworkAssets",
                sql: "\"Role\" IN ('Poster', 'Hero', 'Logo', 'Backdrop')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_Jobs_ArtworkImportIdentity",
                schema: "romd",
                table: "Jobs");

            migrationBuilder.DropCheckConstraint(
                name: "CK_ArtworkSelections_Role",
                schema: "romd",
                table: "ArtworkSelections");

            migrationBuilder.DropCheckConstraint(
                name: "CK_ArtworkPreferences_Role",
                schema: "romd",
                table: "ArtworkPreferences");

            migrationBuilder.DropCheckConstraint(
                name: "CK_ArtworkAssets_Role",
                schema: "romd",
                table: "ArtworkAssets");

            migrationBuilder.DropColumn(
                name: "FillBackdrops",
                schema: "romd",
                table: "ArtworkEnrichmentSettings");

            migrationBuilder.DropColumn(
                name: "ReviewBackdrops",
                schema: "romd",
                table: "ArtworkEnrichmentSettings");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Jobs_ArtworkImportIdentity",
                schema: "romd",
                table: "Jobs",
                sql: "\"JobType\" <> 'artwork-import' OR (\"ArtworkTitleId\" IS NOT NULL AND \"ArtworkTitleId\" > 0 AND \"ArtworkRole\" IS NOT NULL AND \"ArtworkRole\" IN ('Poster', 'Hero', 'Logo') AND \"ArtworkSelectionRevision\" IS NOT NULL AND \"ArtworkSelectionRevision\" > 0 AND \"ArtworkProviderId\" IS NOT NULL AND length(btrim(\"ArtworkProviderId\")) > 0 AND \"ArtworkProviderGameId\" IS NOT NULL AND length(btrim(\"ArtworkProviderGameId\")) > 0 AND \"ArtworkProviderAssetId\" IS NOT NULL AND length(btrim(\"ArtworkProviderAssetId\")) > 0 AND \"ArtworkTrustedAssetUrl\" IS NOT NULL AND length(btrim(\"ArtworkTrustedAssetUrl\")) > 0 AND \"ArtworkWasSuperseded\" IS NOT NULL AND \"Phase\" IN ('Pending', 'Importing', 'Completed', 'Failed', 'Cancelled'))");

            migrationBuilder.AddCheckConstraint(
                name: "CK_ArtworkSelections_Role",
                schema: "romd",
                table: "ArtworkSelections",
                sql: "\"Role\" IN ('Poster', 'Hero', 'Logo')");

            migrationBuilder.AddCheckConstraint(
                name: "CK_ArtworkPreferences_Role",
                schema: "romd",
                table: "ArtworkPreferences",
                sql: "\"Role\" IN ('Poster', 'Hero', 'Logo')");

            migrationBuilder.AddCheckConstraint(
                name: "CK_ArtworkAssets_Role",
                schema: "romd",
                table: "ArtworkAssets",
                sql: "\"Role\" IN ('Poster', 'Hero', 'Logo')");
        }
    }
}
