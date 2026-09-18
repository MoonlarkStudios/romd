using Romd.Domain.ReferenceData;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Romd.Domain.Taxonomy;
using Romd.Persistence.Configuration;

namespace Romd.Persistence.Entities;

public sealed class RegionEntity : EntityBase, ICreatableEntity
{
    public ReferenceOwnership? Ownership { get; set; }
    public int? BuiltInVersion { get; set; }
    public string? BaseName { get; set; }
    public string? BaseDescription { get; set; }
    public int BaseSortOrder { get; set; }
    public bool Retired { get; set; }
    // Effective compatibility facts are refreshed only by catalog publication for registered rows.
    public string? CanonicalKey { get; set; }
    public string Name { get; set; } = null!;
    public int SortOrder { get; set; }
    public bool IsAutoCreated { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public Guid CreatedByUserId { get; set; }

    public static void Configure(EntityTypeBuilder<RegionEntity> builder)
    {
        builder.ConfigureEntity("Regions");
        builder.Property(x => x.Ownership).HasConversion<string>().HasMaxLength(20);
        builder.Property(x => x.BaseName).HasMaxLength(100);
        builder.Property(x => x.BaseDescription).HasMaxLength(2000);
        builder.ToTable("Regions", t =>
        {
            t.HasCheckConstraint("CK_Regions_ReferenceIdentity", "(\"CanonicalKey\" IS NULL AND \"Ownership\" IS NULL AND \"BaseName\" IS NULL AND \"BaseDescription\" IS NULL AND NOT \"Retired\" AND \"BaseSortOrder\" = 0) OR (\"CanonicalKey\" IS NOT NULL AND \"Ownership\" IS NOT NULL AND \"BaseName\" IS NOT NULL)");
            t.HasCheckConstraint("CK_Regions_ReferenceOwnership", "CASE WHEN \"Ownership\" IS NULL THEN \"BuiltInVersion\" IS NULL WHEN \"Ownership\" = 'Romd' THEN \"BuiltInVersion\" IS NOT NULL AND \"BuiltInVersion\" > 0 ELSE false END");
            t.HasCheckConstraint("CK_Regions_BaseSortOrder", "\"BaseSortOrder\" >= 0");
        });
        builder.Property(x => x.CanonicalKey).HasMaxLength(64);
        builder.HasIndex(x => x.CanonicalKey).IsUnique();

        builder.Property(r => r.Name)
            .IsRequired()
            .HasMaxLength(100);

        builder.Property(r => r.SortOrder)
            .IsRequired();

        builder.Property(r => r.IsAutoCreated)
            .IsRequired()
            .HasDefaultValue(false);

        builder.HasIndex(r => r.Name)
            .HasDatabaseName("IX_Regions_Name");
    }

    public Region ToDomain() =>
        Region.Rehydrate(Id, Name, SortOrder, IsAutoCreated);

    public static RegionEntity FromDomain(Region domain) =>
        new()
        {
            Id = domain.Id,
            Name = domain.Name,
            SortOrder = domain.SortOrder,
            IsAutoCreated = domain.IsAutoCreated
        };
}
