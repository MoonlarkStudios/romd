using System.ComponentModel.DataAnnotations.Schema;
using Romd.Domain.ReferenceData;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Romd.Domain.Catalog;
using Romd.Domain.Source.Platform;
using Romd.Persistence.Configuration;

namespace Romd.Persistence.Entities;

public sealed class PlatformEntity : EntityBase, ICreatableEntity
{
    // Local collection preference; reference seeding never writes this field.
    public bool? IsEnabled { get; set; }
    public string? CanonicalKey { get; set; }
    // Null ownership denotes a relational platform not yet registered as reference data.
    public ReferenceOwnership? Ownership { get; set; }
    public int? BuiltInVersion { get; set; }
    public string? BaseName { get; set; }
    public string? BaseCompactLabel { get; set; }
    public string? BaseDescription { get; set; }
    public string? BaseAssetHash { get; set; }
    public bool BaseMonochrome { get; set; }
    public bool Retired { get; set; }

    public string Name { get; set; } = null!;
    public string ShortName { get; set; } = null!;
    // Derived compatibility projection. Only catalog publication writes this for registered systems.
    public string? Manufacturer { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public Guid CreatedByUserId { get; set; }

    // Canonical catalog projection status — managed by CatalogProjectionService, not the
    // Platform domain model (intentionally not round-tripped through ToDomain/FromDomain).
    public CatalogRebuildState CatalogRebuildState { get; set; } = CatalogRebuildState.Clean;
    public DateTimeOffset? CatalogRebuiltAt { get; set; }
    public string? CatalogRebuildError { get; set; }
    public DateTimeOffset? CatalogRebuildFailedAtUtc { get; set; }

    public string? NameOverride { get; set; }
    public bool HasDescriptionOverride { get; set; }
    public string? DescriptionOverride { get; set; }
    public string? CompactLabelOverride { get; set; }
    public bool HasArtworkOverride { get; set; }
    public string? ArtworkOverrideHash { get; set; }
    public bool? MonochromeOverride { get; set; }
    [NotMapped]
    public SystemOverrides ReferenceOverrides
    {
        get => new(NameOverride, CompactLabelOverride, DescriptionOverride, ArtworkOverrideHash, MonochromeOverride, HasDescriptionOverride, HasArtworkOverride);
        set
        {
            NameOverride = value.Name; CompactLabelOverride = value.CompactLabel;
            HasDescriptionOverride = value.HasDescription; DescriptionOverride = value.HasDescription ? value.Description : null;
            HasArtworkOverride = value.HasIcon; ArtworkOverrideHash = value.HasIcon ? value.Icon : null;
            MonochromeOverride = value.Monochrome;
        }
    }
    public static void Configure(EntityTypeBuilder<PlatformEntity> builder)
    {
        builder.ToTable("Platforms", t =>
        {
            t.HasCheckConstraint("CK_Platforms_DescriptionOverride", "\"HasDescriptionOverride\" OR \"DescriptionOverride\" IS NULL");
            t.HasCheckConstraint("CK_Platforms_ArtworkOverride", "\"HasArtworkOverride\" OR \"ArtworkOverrideHash\" IS NULL");
        });
        builder.Property(x => x.NameOverride).HasMaxLength(200);
        builder.Property(x => x.DescriptionOverride).HasMaxLength(2000);
        builder.Property(x => x.CompactLabelOverride).HasMaxLength(40);
        builder.Property(x => x.ArtworkOverrideHash).HasMaxLength(64);
        builder.HasOne<ReferenceAssetEntity>().WithMany().HasForeignKey(x => x.ArtworkOverrideHash).OnDelete(DeleteBehavior.Restrict);
        builder.ConfigureEntity("Platforms");
        builder.ToTable("Platforms", t =>
        {
            t.HasCheckConstraint("CK_Platforms_ReferenceIdentity", "(\"CanonicalKey\" IS NULL AND \"Ownership\" IS NULL AND \"BaseName\" IS NULL AND \"BaseCompactLabel\" IS NULL AND \"BaseDescription\" IS NULL AND \"BaseAssetHash\" IS NULL AND NOT \"Retired\" AND NOT \"BaseMonochrome\") OR (\"CanonicalKey\" IS NOT NULL AND \"Ownership\" IS NOT NULL AND \"BaseName\" IS NOT NULL AND \"BaseCompactLabel\" IS NOT NULL)");
            t.HasCheckConstraint("CK_Platforms_ReferenceOwnership", "CASE WHEN \"Ownership\" IS NULL THEN \"BuiltInVersion\" IS NULL WHEN \"Ownership\" = 'Romd' THEN \"BuiltInVersion\" IS NOT NULL AND \"BuiltInVersion\" > 0 ELSE \"Ownership\" = 'Installation' AND \"BuiltInVersion\" IS NULL END");
        });
        builder.Property(x => x.Ownership).HasConversion<string>().HasMaxLength(20);
        builder.Property(x => x.BaseName).HasMaxLength(200);
        builder.Property(x => x.BaseCompactLabel).HasMaxLength(40);
        builder.Property(x => x.BaseDescription).HasMaxLength(2000);
        builder.Property(x => x.BaseAssetHash).HasMaxLength(64);
        builder.HasOne<ReferenceAssetEntity>().WithMany().HasForeignKey(x => x.BaseAssetHash).OnDelete(DeleteBehavior.Restrict);
        builder.Property(x => x.CanonicalKey).HasMaxLength(64);
        builder.HasIndex(x => x.CanonicalKey).IsUnique();

        builder.Property(p => p.Name)
            .IsRequired()
            .HasMaxLength(200);

        builder.Property(p => p.ShortName)
            .IsRequired()
            .HasMaxLength(50);

        builder.Property(p => p.Manufacturer)
            .HasMaxLength(3230);

        builder.Property(p => p.CatalogRebuildState)
            .IsRequired()
            .HasDefaultValue(CatalogRebuildState.Clean);

        builder.Property(p => p.CatalogRebuildError)
            .HasMaxLength(2000);

        builder.HasIndex(p => p.ShortName)
            .IsUnique();

        builder.HasIndex(p => p.Name);
    }

    public Platform ToDomain()
    {
        return Platform.Rehydrate(
            Id,
            Name,
            ShortName,
            Manufacturer,
            CreatedAt);
    }

    public static PlatformEntity FromDomain(Platform domain)
    {
        return new PlatformEntity
        {
            Id = domain.Id,
            Name = domain.Name,
            ShortName = domain.ShortName,
            Manufacturer = domain.Manufacturer,
            CreatedAt = domain.CreatedAt
        };
    }
}
