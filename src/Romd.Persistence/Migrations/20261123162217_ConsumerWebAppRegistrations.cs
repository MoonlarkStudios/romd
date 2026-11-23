using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Romd.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ConsumerWebAppRegistrations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ConsumerWebAppRegistrations",
                schema: "romd",
                columns: table => new
                {
                    ClientId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    DisplayName = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    CallbackUri = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                    BrowserOrigin = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    ProvisionedAt = table.Column<long>(type: "bigint", nullable: true, defaultValueSql: "NULL"),
                    DisabledAt = table.Column<long>(type: "bigint", nullable: true, defaultValueSql: "NULL"),
                    RetryProvisioningAt = table.Column<long>(type: "bigint", nullable: true, defaultValueSql: "NULL")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ConsumerWebAppRegistrations", x => x.ClientId);
                    table.CheckConstraint("CK_ConsumerWebAppRegistrations_ClientId", " \"ClientId\" ~ '^romd-web-[0-9a-f]{32}$' ");
                });

            migrationBuilder.CreateIndex(
                name: "IX_ConsumerWebAppRegistrations_BrowserOrigin",
                schema: "romd",
                table: "ConsumerWebAppRegistrations",
                column: "BrowserOrigin",
                filter: " \"ProvisionedAt\" IS NOT NULL AND \"DisabledAt\" IS NULL ");

            migrationBuilder.CreateIndex(
                name: "IX_ConsumerWebAppRegistrations_DisplayName_ClientId",
                schema: "romd",
                table: "ConsumerWebAppRegistrations",
                columns: ["DisplayName", "ClientId"]);

            migrationBuilder.Sql("""
                CREATE FUNCTION romd.guard_consumer_web_app_registration() RETURNS trigger
                LANGUAGE plpgsql SET search_path = pg_catalog AS $$
                BEGIN
                    IF OLD."DisabledAt" IS NOT NULL AND NEW."DisabledAt" IS DISTINCT FROM OLD."DisabledAt" THEN
                        RAISE EXCEPTION 'Consumer web app disablement is permanent' USING ERRCODE = '23514';
                    END IF;
                    IF NEW."ProvisionedAt" IS DISTINCT FROM OLD."ProvisionedAt"
                        AND (OLD."ProvisionedAt" IS NOT NULL OR NEW."DisabledAt" IS NOT NULL) THEN
                        RAISE EXCEPTION 'Only a pending consumer web app can be provisioned' USING ERRCODE = '23514';
                    END IF;
                    RETURN NEW;
                END
                $$;
                CREATE TRIGGER guard_consumer_web_app_registration
                    BEFORE UPDATE ON romd."ConsumerWebAppRegistrations"
                    FOR EACH ROW EXECUTE FUNCTION romd.guard_consumer_web_app_registration();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ConsumerWebAppRegistrations",
                schema: "romd");
            migrationBuilder.Sql("DROP FUNCTION romd.guard_consumer_web_app_registration();");
        }
    }
}
