using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Romd.Persistence.Migrations;

/// <summary>Adds an empty server-wide web sign-in message to fresh and existing installations.</summary>
[DbContext(typeof(RomdDbContext))]
[Migration("20261118194850_SignInMessage")]
public sealed class SignInMessage : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "SignInMessage",
            schema: PostgreSqlConfiguration.SchemaName,
            columns: table => new
            {
                Id = table.Column<int>(type: "integer", nullable: false),
                Revision = table.Column<Guid>(type: "uuid", nullable: false),
                Message = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_SignInMessage", row => row.Id);
                table.CheckConstraint("CK_SignInMessage_Singleton", "\"Id\" = 1");
            });

        migrationBuilder.InsertData(
            schema: PostgreSqlConfiguration.SchemaName,
            table: "SignInMessage",
            columns: ["Id", "Message", "Revision"],
            columnTypes: ["integer", "character varying(500)", "uuid"],
            values: [1, string.Empty, Guid.Empty]);
    }

    protected override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.DropTable(name: "SignInMessage", schema: PostgreSqlConfiguration.SchemaName);
}
