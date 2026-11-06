using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Romd.Persistence.Entities;

/// <summary>Title-scoped exclusions require no retained image bytes.</summary>
public sealed class ArtworkExclusionEntity
{
    public int TitleId { get; set; }
    public string SourceUrl { get; set; } = null!;
    public static void Configure(EntityTypeBuilder<ArtworkExclusionEntity> builder)
    {
        builder.ToTable("ArtworkExclusions");
        builder.HasKey(x => new { x.TitleId, x.SourceUrl });
        builder.Property(x => x.SourceUrl).HasMaxLength(2048);
        builder.HasOne<TitleEntity>().WithMany().HasForeignKey(x => x.TitleId).OnDelete(DeleteBehavior.Cascade);
    }
}
