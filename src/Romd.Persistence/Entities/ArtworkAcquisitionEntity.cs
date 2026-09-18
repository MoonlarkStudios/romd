using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Romd.Domain.Catalog;

namespace Romd.Persistence.Entities;

public sealed class ArtworkAcquisitionEntity
{
    public int TitleId { get; set; }
    public ArtworkRole Role { get; set; }
    public string Status { get; set; } = null!;
    public string? SourceId { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    public static void Configure(EntityTypeBuilder<ArtworkAcquisitionEntity> builder)
    {
        builder.ToTable("ArtworkAcquisitions");
        builder.HasKey(x => new { x.TitleId, x.Role });
        builder.Property(x => x.Role).HasConversion<string>().HasMaxLength(20);
        builder.Property(x => x.Status).HasMaxLength(32);
        builder.Property(x => x.SourceId).HasMaxLength(50);
        builder.HasOne<TitleEntity>().WithMany().HasForeignKey(x => x.TitleId).OnDelete(DeleteBehavior.Cascade);
    }
}
