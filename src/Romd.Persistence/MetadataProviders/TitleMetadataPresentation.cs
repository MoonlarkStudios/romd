using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Romd.Admin.Application.MetadataProviders;
using Romd.Domain.Catalog;
using Romd.Persistence.Entities;

namespace Romd.Persistence.MetadataProviders;

/// <summary>Read-only presentation of current provider metadata over the published title facts.</summary>
internal static class TitleMetadataPresentation
{
    internal static IQueryable<TitleEntity> PresentedTitles(this RomdDbContext db) =>
        db.Titles.FromSqlInterpolated($$"""
            SELECT t."Id", t."Revision", t."PlatformId", t."HasLocalPayload", t."RetainWithoutCatalog",
              t."Name", t."NormalizedName", t."SearchDocument", t."SearchVector",
              COALESCE(t."Description", p."MetadataJson" ->> 'Description') AS "Description",
              COALESCE(t."Publisher", p."MetadataJson" ->> 'Publisher') AS "Publisher",
              COALESCE(t."Developer", p."MetadataJson" ->> 'Developer') AS "Developer",
              COALESCE(t."Genre", p."MetadataJson" ->> 'Genre') AS "Genre",
              COALESCE(t."ReleaseDate", (p."MetadataJson" ->> 'ReleaseDate')::date) AS "ReleaseDate",
              COALESCE(t."Players", (p."MetadataJson" ->> 'Players')::integer) AS "Players",
              COALESCE(t."Rating", (p."MetadataJson" ->> 'Rating')::double precision) AS "Rating",
              t."ConservativeMinimumAge", t."EnrichmentStatus", t."LastEnrichedAt", t."CatalogState",
              CASE WHEN p."TitleId" IS NULL THEN t."FieldProvenanceJson" ELSE
                ((CASE WHEN t."FieldProvenanceJson" IS JSON OBJECT THEN t."FieldProvenanceJson"::jsonb ELSE '{}'::jsonb END)
                || jsonb_strip_nulls(jsonb_build_object(
                    'Description', CASE WHEN t."Description" IS NULL AND p."MetadataJson" ->> 'Description' IS NOT NULL THEN p."ProviderId" END,
                    'Publisher', CASE WHEN t."Publisher" IS NULL AND p."MetadataJson" ->> 'Publisher' IS NOT NULL THEN p."ProviderId" END,
                    'Developer', CASE WHEN t."Developer" IS NULL AND p."MetadataJson" ->> 'Developer' IS NOT NULL THEN p."ProviderId" END,
                    'Genre', CASE WHEN t."Genre" IS NULL AND p."MetadataJson" ->> 'Genre' IS NOT NULL THEN p."ProviderId" END,
                    'ReleaseDate', CASE WHEN t."ReleaseDate" IS NULL AND p."MetadataJson" ->> 'ReleaseDate' IS NOT NULL THEN p."ProviderId" END,
                    'Players', CASE WHEN t."Players" IS NULL AND p."MetadataJson" ->> 'Players' IS NOT NULL THEN p."ProviderId" END,
                    'Rating', CASE WHEN t."Rating" IS NULL AND p."MetadataJson" ->> 'Rating' IS NOT NULL THEN p."ProviderId" END)))::text
              END AS "FieldProvenanceJson",
              t."FieldSourceOverridesJson", t."ScreenshotPrefsJson",
              t."CreatedAt", t."CreatedByUserId", t."UpdatedAt", t."UpdatedByUserId"
            FROM romd."Titles" t
            LEFT JOIN romd.current_provisional_title_metadata({{ProviderIdentityPolicy.Version}}, {{ProvisionalProviderMatchPolicy.MetadataVersion}}) p
              ON p."TitleId" = t."Id"
            """).AsNoTracking();

    internal static IQueryable<PresentedTitleRating> PresentedRatings(this RomdDbContext db) =>
        db.Set<PresentedTitleRating>().AsNoTracking();
}

internal sealed class PresentedTitleRating
{
    public int TitleId { get; set; }
    public int Board { get; set; }
    public string Code { get; set; } = null!;
    public int Designation { get; set; }
    public int? MinimumAge { get; set; }
    public string DescriptorsJson { get; set; } = null!;
    public string? Synopsis { get; set; }
    public string SourceId { get; set; } = null!;
    public string? ExternalRatingId { get; set; }

    internal static void Configure(EntityTypeBuilder<PresentedTitleRating> builder)
    {
        builder.HasKey(rating => new { rating.TitleId, rating.Board });
        builder.ToSqlQuery($"SELECT * FROM romd.presented_title_ratings('{ProviderIdentityPolicy.Version}', '{ProvisionalProviderMatchPolicy.MetadataVersion}')");
    }
}
