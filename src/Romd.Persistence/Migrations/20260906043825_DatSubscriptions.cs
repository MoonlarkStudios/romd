using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Romd.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class DatSubscriptions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "DatSubscriptions",
                schema: "romd",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    DatSourceId = table.Column<int>(type: "integer", nullable: false),
                    CatalogId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    State = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    LastCheckedAt = table.Column<long>(type: "bigint", nullable: true),
                    Message = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    CandidateFileId = table.Column<int>(type: "integer", nullable: true),
                    CandidateSha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    ActiveSha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    JobId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DatSubscriptions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DatSubscriptions_DatSources_DatSourceId",
                        column: x => x.DatSourceId,
                        principalSchema: "romd",
                        principalTable: "DatSources",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_DatSubscriptions_Files_CandidateFileId",
                        column: x => x.CandidateFileId,
                        principalSchema: "romd",
                        principalTable: "Files",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DatSubscriptions_CandidateFileId",
                schema: "romd",
                table: "DatSubscriptions",
                column: "CandidateFileId");

            migrationBuilder.CreateIndex(
                name: "IX_DatSubscriptions_DatSourceId",
                schema: "romd",
                table: "DatSubscriptions",
                column: "DatSourceId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DatSubscriptions",
                schema: "romd");
        }
    }
}
