using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Romd.Domain.Catalog;
using Romd.Persistence.Configuration;

namespace Romd.Persistence.Entities;

public sealed class TitleMetadataLayerEntity : EntityBase, IEditableEntity
{
    public int TitleId { get; set; }
    public string SourceId { get; set; } = null!;
    public int SourceType { get; set; }
    public string MetadataJson { get; set; } = "{}";
    public DateTimeOffset CreatedAt { get; set; }
    public Guid CreatedByUserId { get; set; }
    public DateTimeOffset? UpdatedAt { get; set; }
    public Guid? UpdatedByUserId { get; set; }

    public TitleEntity? Title { get; set; }

    public static void Configure(EntityTypeBuilder<TitleMetadataLayerEntity> builder)
    {
        builder.ConfigureEntity("TitleMetadataLayers");

        builder.Property(l => l.SourceId)
            .IsRequired()
            .HasMaxLength(50);

        builder.Property(l => l.MetadataJson)
            .IsRequired()
            .HasColumnType("TEXT");

        builder.HasIndex(l => new { l.TitleId, l.SourceId })
            .IsUnique();
    }

    public TitleMetadataLayer ToDomain()
    {
        return TitleMetadataLayer.Rehydrate(
            id: Id,
            titleId: TitleId,
            sourceId: SourceId,
            sourceType: (MetadataSourceType)SourceType,
            metadataJson: MetadataJson,
            updatedAt: UpdatedAt ?? CreatedAt);
    }

    public static TitleMetadataLayerEntity FromDomain(TitleMetadataLayer domain)
    {
        return new TitleMetadataLayerEntity
        {
            Id = domain.Id,
            TitleId = domain.TitleId,
            SourceId = domain.SourceId,
            SourceType = (int)domain.SourceType,
            MetadataJson = domain.MetadataJson,
            UpdatedAt = domain.UpdatedAt
        };
    }
}
