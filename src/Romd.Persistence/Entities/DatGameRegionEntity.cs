using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Romd.Persistence.Entities;

public sealed class DatGameRegionEntity : ICreatableEntity
{
    public int DatGameId { get; set; }
    public int RegionId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public Guid CreatedByUserId { get; set; }

    public static void Configure(EntityTypeBuilder<DatGameRegionEntity> builder)
    {
        builder.ToTable("DatGameRegions");
        builder.HasKey(e => new { e.DatGameId, e.RegionId });

        builder.HasIndex(e => e.RegionId)
            .HasDatabaseName("IX_DatGameRegions_RegionId");

        builder.HasOne<DatGameEntity>()
            .WithMany()
            .HasForeignKey(e => e.DatGameId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne<RegionEntity>()
            .WithMany()
            .HasForeignKey(e => e.RegionId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
