using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Romd.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class RuntimeIgdbProviderSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "MetadataProviderSettings",
                schema: "romd",
                columns: table => new
                {
                    ProviderId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false),
                    ClientId = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    ProtectedClientSecret = table.Column<string>(type: "text", nullable: true),
                    Revision = table.Column<Guid>(type: "uuid", nullable: false),
                    LastTestedAt = table.Column<long>(type: "bigint", nullable: true),
                    LastTestSucceeded = table.Column<bool>(type: "boolean", nullable: true),
                    LastTestMessage = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    TestConfigurationFingerprint = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MetadataProviderSettings", x => x.ProviderId);
                });

            // Default privileges grant consumer SELECT on new tables. Revoke it in the
            // same transaction as creation, before any credentials can be stored.
            migrationBuilder.Sql("""
                DO $grants$
                BEGIN
                    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'romd_consumer') THEN
                        REVOKE ALL ON TABLE romd."MetadataProviderSettings" FROM romd_consumer;
                    END IF;
                END $grants$;
                """);

            migrationBuilder.InsertData(
                schema: "romd",
                table: "MetadataProviderSettings",
                columns: new[] { "ProviderId", "ClientId", "Enabled", "LastTestMessage", "LastTestSucceeded", "LastTestedAt", "ProtectedClientSecret", "Revision", "TestConfigurationFingerprint" },
                values: new object[] { "igdb", null, false, null, null, null, null, new Guid("00000000-0000-0000-0000-000000000000"), null });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MetadataProviderSettings",
                schema: "romd");
        }
    }
}
