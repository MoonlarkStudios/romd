using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Romd.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ScheduledDatChecks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "ConsecutiveCheckFailures",
                schema: "romd",
                table: "DatSubscriptions",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<long>(
                name: "NextCheckAt",
                schema: "romd",
                table: "DatSubscriptions",
                type: "bigint",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ConsecutiveCheckFailures",
                schema: "romd",
                table: "DatSubscriptions");

            migrationBuilder.DropColumn(
                name: "NextCheckAt",
                schema: "romd",
                table: "DatSubscriptions");
        }
    }
}
