using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Romd.Persistence.Entities;

/// <summary>
///     Language taxonomy of a canonical release, projected from the representative source so
///     consumer reads never touch source DAT taxonomy tables.
/// </summary>
public sealed class CatalogReleaseLanguageEntity
{
    public int CatalogReleaseId { get; set; }
    public int GameLanguageId { get; set; }

    public static void Configure(EntityTypeBuilder<CatalogReleaseLanguageEntity> builder)
    {
        builder.ToTable("CatalogReleaseLanguages");
        builder.HasKey(e => new { e.CatalogReleaseId, e.GameLanguageId });

        builder.HasIndex(e => e.GameLanguageId)
            .HasDatabaseName("IX_CatalogReleaseLanguages_GameLanguageId");

        builder.HasOne<CatalogReleaseEntity>()
            .WithMany()
            .HasForeignKey(e => e.CatalogReleaseId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne<GameLanguageEntity>()
            .WithMany()
            .HasForeignKey(e => e.GameLanguageId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
