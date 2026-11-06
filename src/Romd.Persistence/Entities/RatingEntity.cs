using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Romd.Domain.ReferenceData;

namespace Romd.Persistence.Entities;

public sealed class RatingEntity
{
    public string Key { get; set; } = null!;
    public ReferenceOwnership Ownership { get; set; }
    public int? BuiltInVersion { get; set; }
    public string BaseName { get; set; } = null!;
    public string? BaseDescription { get; set; }
    public bool Retired { get; set; }
    public string BoardKey { get; set; } = null!;
    public RatingBoardEntity Board { get; set; } = null!;
    public string Code { get; set; } = null!;
    public string? Designation { get; set; }
    public int? MinimumAge { get; set; }
    public string? BaseAssetHash { get; set; }
    public bool BaseMonochrome { get; set; }
    public static void Configure(EntityTypeBuilder<RatingEntity> b)
    {
        b.ToTable("Ratings", t => t.HasCheckConstraint("CK_Ratings_ReferenceOwnership", "(\"Ownership\" = 'Romd' AND \"BuiltInVersion\" IS NOT NULL AND \"BuiltInVersion\" > 0)"));
        b.ToTable("Ratings", t => t.HasCheckConstraint("CK_Ratings_MinimumAge", "\"MinimumAge\" IS NULL OR \"MinimumAge\" >= 0"));
        b.HasKey(x => x.Key);
        b.Property(x => x.Key).HasMaxLength(128);
        b.Property(x => x.Ownership).HasConversion<string>().HasMaxLength(20);
        b.Property(x => x.BaseName).HasMaxLength(200).IsRequired();
        b.Property(x => x.BaseDescription).HasMaxLength(2000);
        b.Property(x => x.BoardKey).HasMaxLength(64);
        b.Property(x => x.Code).HasMaxLength(32);
        b.Property(x => x.Designation).HasMaxLength(64);
        b.Property(x => x.BaseAssetHash).HasMaxLength(64);
        b.HasIndex(x => new { x.BoardKey, x.Code }).IsUnique();
        b.HasOne(x => x.Board).WithMany().HasForeignKey(x => x.BoardKey).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<ReferenceAssetEntity>().WithMany().HasForeignKey(x => x.BaseAssetHash).OnDelete(DeleteBehavior.Restrict);
        b.ToTable("Ratings", t => t.HasCheckConstraint("CK_Ratings_Identity", "\"Key\" = \"BoardKey\" || ':' || \"Code\""));
    }
}
