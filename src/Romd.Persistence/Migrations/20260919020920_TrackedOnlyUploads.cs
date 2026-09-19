using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Romd.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class TrackedOnlyUploads : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "TrackedOnly",
                schema: "romd",
                table: "Jobs",
                type: "boolean",
                nullable: true);

            // TPH leaves this column nullable for other job kinds; historical uploads
            // must retain the old import-everything behavior when materialized as bool.
            migrationBuilder.Sql("UPDATE romd.\"Jobs\" SET \"TrackedOnly\" = FALSE WHERE \"JobType\" = 'upload'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "TrackedOnly",
                schema: "romd",
                table: "Jobs");
        }
    }
}
