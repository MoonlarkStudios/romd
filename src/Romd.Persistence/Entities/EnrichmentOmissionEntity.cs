using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
namespace Romd.Persistence.Entities;

/// <summary>Explicit curator acceptance of an absent core field, not a metadata value or job result.</summary>
public sealed class EnrichmentOmissionEntity
{
    public int TitleId { get; set; }
    public string Field { get; set; } = null!;
    public DateTimeOffset AcceptedAt { get; set; }
    public static void Configure(EntityTypeBuilder<EnrichmentOmissionEntity> builder)
    {
        builder.ToTable("EnrichmentOmissions", table => table.HasCheckConstraint("CK_EnrichmentOmissions_Field", "\"Field\" IN ('Description', 'Genre', 'ReleaseDate')"));
        builder.HasKey(x => new { x.TitleId, x.Field });
        builder.Property(x => x.Field).HasMaxLength(32);
        builder.HasOne<TitleEntity>().WithMany().HasForeignKey(x => x.TitleId).OnDelete(DeleteBehavior.Cascade);
    }
}
