using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Romd.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ReferenceDataUpdates : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CandidateDocument",
                schema: "romd",
                table: "ReferenceDataState",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CandidateHash",
                schema: "romd",
                table: "ReferenceDataState",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "CandidateVersion",
                schema: "romd",
                table: "ReferenceDataState",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LastApplyResult",
                schema: "romd",
                table: "ReferenceDataState",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LastCheckMessage",
                schema: "romd",
                table: "ReferenceDataState",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "LastCheckedAt",
                schema: "romd",
                table: "ReferenceDataState",
                type: "bigint",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CandidateDocument",
                schema: "romd",
                table: "ReferenceDataState");

            migrationBuilder.DropColumn(
                name: "CandidateHash",
                schema: "romd",
                table: "ReferenceDataState");

            migrationBuilder.DropColumn(
                name: "CandidateVersion",
                schema: "romd",
                table: "ReferenceDataState");

            migrationBuilder.DropColumn(
                name: "LastApplyResult",
                schema: "romd",
                table: "ReferenceDataState");

            migrationBuilder.DropColumn(
                name: "LastCheckMessage",
                schema: "romd",
                table: "ReferenceDataState");

            migrationBuilder.DropColumn(
                name: "LastCheckedAt",
                schema: "romd",
                table: "ReferenceDataState");
        }
    }
}
