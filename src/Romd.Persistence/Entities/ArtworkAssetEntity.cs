using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Romd.Domain.Catalog;
using Romd.Persistence.Configuration;

namespace Romd.Persistence.Entities;

/// <summary>Retained artwork is separate from historical media upsert semantics.</summary>
public sealed class ArtworkAssetEntity : EntityBase
{
    public int TitleId { get; set; }
    public ArtworkRole Role { get; set; }
    public string SourceId { get; set; } = null!;
    public string? ProviderGameId { get; set; }
    public string? ProviderAssetId { get; set; }
    public int OriginalFileId { get; set; }
    public string ContentVersion { get; set; } = null!;
    public string ContentType { get; set; } = null!;
    public int Width { get; set; }
    public int Height { get; set; }
    public bool IsEligible { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public string? Attribution { get; set; }
    public string? SourcePageUrl { get; set; }
    public List<ArtworkVariantEntity> Variants { get; set; } = [];

    public static void Configure(EntityTypeBuilder<ArtworkAssetEntity> builder)
    {
        builder.ConfigureEntity("ArtworkAssets");
        builder.Property(a => a.Role).HasConversion<string>().HasMaxLength(20);
        builder.Property(a => a.SourceId).HasMaxLength(50);
        builder.Property(a => a.ProviderGameId).HasMaxLength(100);
        builder.Property(a => a.ProviderAssetId).HasMaxLength(100);
        builder.Property(a => a.ContentVersion).HasMaxLength(100);
        builder.Property(a => a.ContentType).HasMaxLength(100);
        builder.Property(a => a.Attribution).HasMaxLength(500);
        builder.Property(a => a.SourcePageUrl).HasMaxLength(1000);
        builder.HasAlternateKey(a => new { a.Id, a.TitleId, a.Role });
        builder.HasIndex(a => new { a.TitleId, a.Role, a.SourceId, a.ProviderAssetId, a.ContentVersion })
            .IsUnique().AreNullsDistinct(false);
        builder.HasOne<TitleEntity>().WithMany().HasForeignKey(a => a.TitleId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<FileEntityPersistence>().WithMany().HasForeignKey(a => a.OriginalFileId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasMany(a => a.Variants).WithOne().HasForeignKey(v => v.AssetId).OnDelete(DeleteBehavior.Cascade);
        builder.ToTable(t =>
        {
            t.HasCheckConstraint("CK_ArtworkAssets_Dimensions", "\"Width\" > 0 AND \"Height\" > 0");
            t.HasCheckConstraint("CK_ArtworkAssets_Role", "\"Role\" IN ('Poster', 'Hero', 'Logo', 'Backdrop')");
        });
    }

    public ArtworkAsset ToDomain() => new(Id, TitleId, Role, SourceId, ProviderGameId, ProviderAssetId,
        new ArtworkVariant(OriginalFileId, ContentVersion, ContentType, Width, Height, "original"),
        Variants.Select(v => v.ToDomain()), IsEligible, CreatedAt, Attribution, SourcePageUrl);
}
