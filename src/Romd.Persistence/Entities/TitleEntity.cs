using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NpgsqlTypes;
using Romd.Domain.Catalog;
using Romd.Persistence.Configuration;
using Romd.Persistence.Search;

namespace Romd.Persistence.Entities;

public sealed class TitleEntity : EntityBase, IEditableEntity
{
    public Guid Revision { get; set; } = Guid.NewGuid();

    public int PlatformId { get; set; }
    /// <summary>
    ///     Catalog-owned projection indicating that at least one effective source entry for
    ///     this title currently asserts locally stored payload. Generic title updates must not
    ///     write this value; <c>TitlePayloadAvailabilityProjection</c> is its sole owner.
    /// </summary>
    public bool HasLocalPayload { get; set; }
    /// <summary>Remember locally owned identity when a source is withdrawn; this is not current availability.</summary>
    public bool RetainWithoutCatalog { get; set; }
    public string Name { get; set; } = null!;
    public string NormalizedName { get; set; } = null!;
    public string SearchDocument { get; set; } = string.Empty;
    public NpgsqlTsVector SearchVector { get; set; } = null!;
    public string? Description { get; set; }
    public string? Publisher { get; set; }
    public string? Developer { get; set; }
    public string? Genre { get; set; }
    public DateOnly? ReleaseDate { get; set; }
    public int? Players { get; set; }
    public double? Rating { get; set; }
    public int? ConservativeMinimumAge { get; set; }
    public string EnrichmentStatus { get; set; } = null!;
    public DateTimeOffset? LastEnrichedAt { get; set; }
    public TitleCatalogState CatalogState { get; set; } = TitleCatalogState.Active;
    public string FieldProvenanceJson { get; set; } = "{}";
    public string FieldSourceOverridesJson { get; set; } = "{}";
    // TODO: Add ScreenshotPreferences type when media resolution preferences are implemented
    public string ScreenshotPrefsJson { get; set; } = "{}";
    public DateTimeOffset CreatedAt { get; set; }
    public Guid CreatedByUserId { get; set; }
    public DateTimeOffset? UpdatedAt { get; set; }
    public Guid? UpdatedByUserId { get; set; }
    public List<TitleExternalIdEntity> ExternalIds { get; set; } = [];
    public List<TitleMediaEntity> Media { get; set; } = [];
    public List<TitleMetadataLayerEntity> MetadataLayers { get; set; } = [];
    public List<TitleContentRatingEntity> ContentRatings { get; set; } = [];

    public static void Configure(EntityTypeBuilder<TitleEntity> builder)
    {
        builder.ConfigureEntity("Titles");
        builder.Property(t => t.Revision).IsConcurrencyToken();

        builder.Property(t => t.Name)
            .IsRequired()
            .HasMaxLength(500);

        builder.Property(t => t.NormalizedName)
            .IsRequired()
            .HasMaxLength(500);

        builder.Property(t => t.Description)
            .HasMaxLength(4000);

        SearchDocuments.Configure(builder);

        builder.Property(t => t.Publisher)
            .HasMaxLength(500);

        builder.Property(t => t.Developer)
            .HasMaxLength(500);

        builder.Property(t => t.Genre)
            .HasMaxLength(200);

        builder.Property(t => t.EnrichmentStatus)
            .IsRequired()
            .HasMaxLength(50);

        builder.Property(t => t.CatalogState)
            .IsRequired()
            .HasDefaultValue(TitleCatalogState.Active);

        builder.Property(t => t.FieldProvenanceJson)
            .IsRequired()
            .HasColumnType("TEXT");

        builder.Property(t => t.FieldSourceOverridesJson)
            .IsRequired()
            .HasColumnType("TEXT")
            .HasDefaultValue("{}");

        builder.Property(t => t.ScreenshotPrefsJson)
            .IsRequired()
            .HasColumnType("TEXT")
            .HasDefaultValue("{}");

        builder.HasIndex(t => new { t.PlatformId, t.NormalizedName })
            .IsUnique();

        builder.HasIndex(t => t.EnrichmentStatus);
        builder.HasIndex(t => t.ConservativeMinimumAge);

        // Index for catalog-state-scoped queries (e.g. pruning, UserOnly titles) per platform.
        builder.HasIndex(t => new { t.PlatformId, t.CatalogState });

        builder.HasIndex(t => new { t.PlatformId, t.HasLocalPayload })
            .HasDatabaseName("IX_Titles_PlatformId_HasLocalPayload");

        builder.HasOne<PlatformEntity>()
            .WithMany()
            .HasForeignKey(t => t.PlatformId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasMany(t => t.ExternalIds)
            .WithOne()
            .HasForeignKey(e => e.TitleId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasMany(t => t.Media)
            .WithOne()
            .HasForeignKey(m => m.TitleId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasMany(t => t.MetadataLayers)
            .WithOne(l => l.Title)
            .HasForeignKey(l => l.TitleId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(x => x.Genre);
        builder.HasIndex(x => new { x.Name, x.Id });
    }

    public Title ToDomain()
    {
        if (!Enum.TryParse<EnrichmentStatus>(EnrichmentStatus, out var status))
        {
            status = Domain.Catalog.EnrichmentStatus.None;
        }

        return Title.Rehydrate(
            Id,
            PlatformId,
            Name,
            NormalizedName,
            Description,
            Publisher,
            Developer,
            Genre,
            ReleaseDate,
            Players,
            Rating,
            status,
            LastEnrichedAt,
            CreatedAt,
            null,
            null,
            fieldProvenance: JsonSerializer.Deserialize<Dictionary<string, string>>(FieldProvenanceJson),
            fieldSourceOverrides: JsonSerializer.Deserialize<Dictionary<string, string>>(FieldSourceOverridesJson),
            conservativeMinimumAge: ConservativeMinimumAge,
            revision: Revision);
    }

    public Title ToDomainWithCollections()
    {
        if (!Enum.TryParse<EnrichmentStatus>(EnrichmentStatus, out var status))
        {
            status = Domain.Catalog.EnrichmentStatus.None;
        }

        Dictionary<string, string>? provenance = null;
        if (!string.IsNullOrWhiteSpace(FieldProvenanceJson) && FieldProvenanceJson != "{}")
        {
            provenance = JsonSerializer.Deserialize<Dictionary<string, string>>(FieldProvenanceJson);
        }

        Dictionary<string, string>? overrides = null;
        if (!string.IsNullOrWhiteSpace(FieldSourceOverridesJson) && FieldSourceOverridesJson != "{}")
        {
            overrides = JsonSerializer.Deserialize<Dictionary<string, string>>(FieldSourceOverridesJson);
        }

        return Title.Rehydrate(
            Id,
            PlatformId,
            Name,
            NormalizedName,
            Description,
            Publisher,
            Developer,
            Genre,
            ReleaseDate,
            Players,
            Rating,
            status,
            LastEnrichedAt,
            CreatedAt,
            ExternalIds.Select(e => e.ToDomain()),
            Media.Select(m => m.ToDomain()),
            MetadataLayers.Select(l => l.ToDomain()),
            provenance,
            overrides,
            ContentRatings.Select(r => r.ToDomain()),
            ConservativeMinimumAge,
            Revision);
    }

    public static TitleEntity FromDomain(Title domain)
    {
        string provenanceJson = domain.FieldProvenance.Count > 0
            ? JsonSerializer.Serialize(domain.FieldProvenance)
            : "{}";

        string overridesJson = domain.FieldSourceOverrides.Count > 0
            ? JsonSerializer.Serialize(domain.FieldSourceOverrides)
            : "{}";

        var entity = new TitleEntity
        {
            Id = domain.Id,
            Revision = domain.Revision,
            PlatformId = domain.PlatformId,
            Name = domain.Name,
            NormalizedName = domain.NormalizedName,
            Description = domain.Description,
            Publisher = domain.Publisher,
            Developer = domain.Developer,
            Genre = domain.Genre,
            ReleaseDate = domain.ReleaseDate,
            Players = domain.Players,
            Rating = domain.Rating,
            ConservativeMinimumAge = domain.ConservativeMinimumAge,
            EnrichmentStatus = domain.EnrichmentStatus.ToString(),
            LastEnrichedAt = domain.LastEnrichedAt,
            CreatedAt = domain.CreatedAt,
            FieldProvenanceJson = provenanceJson,
            FieldSourceOverridesJson = overridesJson
        };

        entity.ExternalIds.AddRange(domain.ExternalIds.Select(TitleExternalIdEntity.FromDomain));
        entity.Media.AddRange(domain.Media.Select(TitleMediaEntity.FromDomain));
        entity.MetadataLayers.AddRange(domain.MetadataLayers.Select(TitleMetadataLayerEntity.FromDomain));
        // ContentRatings are intentionally NOT attached to the graph: domain ratings carry no
        // database identity, so TitleRepository syncs them by (TitleId, Board) instead.

        return entity;
    }
}
