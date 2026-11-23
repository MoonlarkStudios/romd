using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Romd.Persistence.Migrations;

[DbContext(typeof(RomdDbContext))]
[Migration("20261123040000_ProvisionalProviderArtwork")]
public sealed class ProvisionalProviderArtwork : Migration
{
    private const string PosterConstraint = "CK_ArtworkAssets_ProvisionalPoster";
    private const string ProviderConstraint = "CK_ArtworkAssets_ProvisionalProvider";
    private const string Table = "ArtworkAssets";
    private const string Schema = "romd";

    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropCheckConstraint(PosterConstraint, Table, Schema);
        migrationBuilder.AddCheckConstraint(ProviderConstraint, Table,
            """NOT "IsProvisional" OR (NOT "IsEligible" AND "ProviderGameId" IS NOT NULL AND "ProviderAssetId" IS NOT NULL)""",
            Schema);
        migrationBuilder.Sql("""
            ALTER FUNCTION romd.provider_provisional_poster_game(integer, text, text, text)
            RENAME TO provider_provisional_game;
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            DELETE FROM romd."ArtworkAssets"
            WHERE "IsProvisional" AND ("Role" <> 'Poster' OR "SourceId" <> 'steamgriddb');
            ALTER FUNCTION romd.provider_provisional_game(integer, text, text, text)
            RENAME TO provider_provisional_poster_game;
            """);
        migrationBuilder.DropCheckConstraint(ProviderConstraint, Table, Schema);
        migrationBuilder.AddCheckConstraint(PosterConstraint, Table,
            """NOT "IsProvisional" OR (NOT "IsEligible" AND "Role" = 'Poster' AND "SourceId" = 'steamgriddb' AND "ProviderGameId" IS NOT NULL AND "ProviderAssetId" IS NOT NULL)""",
            Schema);
    }
}
