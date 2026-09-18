using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Romd.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ArtworkImportPipeline : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ArtworkAttribution",
                schema: "romd",
                table: "Jobs",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ArtworkProviderAssetId",
                schema: "romd",
                table: "Jobs",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ArtworkProviderGameId",
                schema: "romd",
                table: "Jobs",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ArtworkProviderId",
                schema: "romd",
                table: "Jobs",
                type: "character varying(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ArtworkRetainedAssetId",
                schema: "romd",
                table: "Jobs",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ArtworkRole",
                schema: "romd",
                table: "Jobs",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "ArtworkSelectionRevision",
                schema: "romd",
                table: "Jobs",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ArtworkTitleId",
                schema: "romd",
                table: "Jobs",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ArtworkTrustedAssetUrl",
                schema: "romd",
                table: "Jobs",
                type: "character varying(2048)",
                maxLength: 2048,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "ArtworkWasSuperseded",
                schema: "romd",
                table: "Jobs",
                type: "boolean",
                nullable: true);

            migrationBuilder.InsertData(
                schema: "romd",
                table: "MetadataProviderSettings",
                columns: new[] { "ProviderId", "ClientId", "Enabled", "LastTestMessage", "LastTestSucceeded", "LastTestedAt", "ProtectedClientSecret", "Revision", "TestConfigurationFingerprint" },
                values: new object[] { "steamgriddb", null, false, null, null, null, null, new Guid("00000000-0000-0000-0000-000000000000"), null });

            migrationBuilder.AddCheckConstraint(
                name: "CK_Jobs_ArtworkImportIdentity",
                schema: "romd",
                table: "Jobs",
                sql: "\"JobType\" <> 'artwork-import' OR (\"ArtworkTitleId\" IS NOT NULL AND \"ArtworkTitleId\" > 0 AND \"ArtworkRole\" IS NOT NULL AND \"ArtworkRole\" IN ('Poster', 'Hero') AND \"ArtworkSelectionRevision\" IS NOT NULL AND \"ArtworkSelectionRevision\" > 0 AND \"ArtworkProviderId\" IS NOT NULL AND length(btrim(\"ArtworkProviderId\")) > 0 AND \"ArtworkProviderGameId\" IS NOT NULL AND length(btrim(\"ArtworkProviderGameId\")) > 0 AND \"ArtworkProviderAssetId\" IS NOT NULL AND length(btrim(\"ArtworkProviderAssetId\")) > 0 AND \"ArtworkTrustedAssetUrl\" IS NOT NULL AND length(btrim(\"ArtworkTrustedAssetUrl\")) > 0 AND \"ArtworkWasSuperseded\" IS NOT NULL AND \"Phase\" IN ('Pending', 'Importing', 'Completed', 'Failed', 'Cancelled'))");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // The previous model cannot materialize this discriminator. Remove its
            // jobs (and cascading dispatch records) before dropping their fields.
            migrationBuilder.Sql("UPDATE romd.\"ArtworkSelections\" SET \"PendingRequestId\" = NULL WHERE \"PendingRequestId\" IN (SELECT \"Id\" FROM romd.\"Jobs\" WHERE \"JobType\" = 'artwork-import');");
            migrationBuilder.Sql("DELETE FROM romd.\"Jobs\" WHERE \"JobType\" = 'artwork-import';");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Jobs_ArtworkImportIdentity",
                schema: "romd",
                table: "Jobs");

            migrationBuilder.DeleteData(
                schema: "romd",
                table: "MetadataProviderSettings",
                keyColumn: "ProviderId",
                keyValue: "steamgriddb");

            migrationBuilder.DropColumn(
                name: "ArtworkAttribution",
                schema: "romd",
                table: "Jobs");

            migrationBuilder.DropColumn(
                name: "ArtworkProviderAssetId",
                schema: "romd",
                table: "Jobs");

            migrationBuilder.DropColumn(
                name: "ArtworkProviderGameId",
                schema: "romd",
                table: "Jobs");

            migrationBuilder.DropColumn(
                name: "ArtworkProviderId",
                schema: "romd",
                table: "Jobs");

            migrationBuilder.DropColumn(
                name: "ArtworkRetainedAssetId",
                schema: "romd",
                table: "Jobs");

            migrationBuilder.DropColumn(
                name: "ArtworkRole",
                schema: "romd",
                table: "Jobs");

            migrationBuilder.DropColumn(
                name: "ArtworkSelectionRevision",
                schema: "romd",
                table: "Jobs");

            migrationBuilder.DropColumn(
                name: "ArtworkTitleId",
                schema: "romd",
                table: "Jobs");

            migrationBuilder.DropColumn(
                name: "ArtworkTrustedAssetUrl",
                schema: "romd",
                table: "Jobs");

            migrationBuilder.DropColumn(
                name: "ArtworkWasSuperseded",
                schema: "romd",
                table: "Jobs");
        }
    }
}
