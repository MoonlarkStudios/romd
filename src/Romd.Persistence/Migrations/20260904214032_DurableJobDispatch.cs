using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Romd.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class DurableJobDispatch : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "JobDispatches",
                schema: "romd",
                columns: table => new
                {
                    JobId = table.Column<Guid>(type: "uuid", nullable: false),
                    JobType = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    CreatedAtUtc = table.Column<long>(type: "bigint", nullable: false),
                    AvailableAtUtc = table.Column<long>(type: "bigint", nullable: false),
                    ClaimToken = table.Column<Guid>(type: "uuid", nullable: true),
                    LeaseExpiresAtUtc = table.Column<long>(type: "bigint", nullable: true),
                    DeliveredAtUtc = table.Column<long>(type: "bigint", nullable: true),
                    HangfireJobId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    AttemptCount = table.Column<int>(type: "integer", nullable: false),
                    LastError = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_JobDispatches", x => x.JobId);
                    table.ForeignKey(
                        name: "FK_JobDispatches_Jobs_JobId",
                        column: x => x.JobId,
                        principalSchema: "romd",
                        principalTable: "Jobs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_JobDispatches_DeliveredAtUtc_AvailableAtUtc_JobId",
                schema: "romd",
                table: "JobDispatches",
                columns: new[] { "DeliveredAtUtc", "AvailableAtUtc", "JobId" });

            // Upgrade recovery uses the same identity as new acceptance. Preserve visible
            // deliveries while allowing their abandoned execution leases to recover too.
            migrationBuilder.Sql("""
                INSERT INTO romd."JobDispatches"
                    ("JobId", "JobType", "CreatedAtUtc", "AvailableAtUtc", "AttemptCount", "HangfireJobId", "DeliveredAtUtc")
                SELECT "Id", "JobType", "CreatedAt", "CreatedAt", 0, "HangfireJobId",
                    CASE WHEN "HangfireJobId" IS NULL OR "HangfireJobId" = '' THEN NULL ELSE "UpdatedAt" END
                FROM romd."Jobs"
                WHERE "Phase" NOT IN ('Completed', 'CompletedWithErrors', 'Failed', 'Cancelled', 'Deferred')
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "JobDispatches",
                schema: "romd");
        }
    }
}
