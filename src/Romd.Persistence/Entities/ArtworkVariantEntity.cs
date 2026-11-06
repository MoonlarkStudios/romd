using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Romd.Domain.Catalog;

namespace Romd.Persistence.Entities;

public sealed class ArtworkVariantEntity
{
    public int AssetId { get; set; }
    public string Name { get; set; } = null!;
    public int FileId { get; set; }
    public string ContentVersion { get; set; } = null!;
    public string ContentType { get; set; } = null!;
    public int Width { get; set; }
    public int Height { get; set; }

    public static void Configure(EntityTypeBuilder<ArtworkVariantEntity> builder)
    {
        builder.ToTable("ArtworkVariants");
        builder.HasKey(v => new { v.AssetId, v.Name });
        builder.Property(v => v.Name).HasMaxLength(30);
        builder.Property(v => v.ContentVersion).HasMaxLength(100);
        builder.Property(v => v.ContentType).HasMaxLength(100);
        builder.HasOne<FileEntityPersistence>().WithMany().HasForeignKey(v => v.FileId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.ToTable(t => t.HasCheckConstraint("CK_ArtworkVariants_Dimensions", "\"Width\" > 0 AND \"Height\" > 0"));
    }

    public ArtworkVariant ToDomain() => new(FileId, ContentVersion, ContentType, Width, Height, Name);
}
