using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Romd.Persistence.Entities;

/// <summary>
///     Region taxonomy of a canonical release, projected from the representative source so
///     consumer reads never touch source DAT taxonomy tables.
/// </summary>
public sealed class CatalogReleaseRegionEntity
{
    public int CatalogReleaseId { get; set; }
    public int RegionId { get; set; }

    public static void Configure(EntityTypeBuilder<CatalogReleaseRegionEntity> builder)
    {
        builder.ToTable("CatalogReleaseRegions");
        builder.HasKey(e => new { e.CatalogReleaseId, e.RegionId });

        builder.HasIndex(e => e.RegionId)
            .HasDatabaseName("IX_CatalogReleaseRegions_RegionId");

        builder.HasOne<CatalogReleaseEntity>()
            .WithMany()
            .HasForeignKey(e => e.CatalogReleaseId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne<RegionEntity>()
            .WithMany()
            .HasForeignKey(e => e.RegionId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
