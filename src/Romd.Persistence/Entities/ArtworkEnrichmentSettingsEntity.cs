using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Romd.Persistence.Entities;

public sealed class ArtworkEnrichmentSettingsEntity
{
    public int Id { get; set; } = 1;
    public Guid Revision { get; set; }
    public bool FillPosters { get; set; } = true;
    public bool FillHeroes { get; set; } = true;
    public bool FillLogos { get; set; } = true;
    public bool FillBackdrops { get; set; } = true;
    public bool ReviewBackdrops { get; set; } = true;

    public static void Configure(EntityTypeBuilder<ArtworkEnrichmentSettingsEntity> builder)
    {
        builder.ToTable("ArtworkEnrichmentSettings", table => table.HasCheckConstraint("CK_ArtworkEnrichmentSettings_Singleton", "\"Id\" = 1"));
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedNever();
        builder.Property(x => x.Revision).IsConcurrencyToken();
        builder.HasData(new ArtworkEnrichmentSettingsEntity());
    }
}
