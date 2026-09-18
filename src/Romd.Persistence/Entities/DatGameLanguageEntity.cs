using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Romd.Persistence.Entities;

public sealed class DatGameLanguageEntity : ICreatableEntity
{
    public int DatGameId { get; set; }
    public int GameLanguageId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public Guid CreatedByUserId { get; set; }

    public static void Configure(EntityTypeBuilder<DatGameLanguageEntity> builder)
    {
        builder.ToTable("DatGameLanguages");
        builder.HasKey(e => new { e.DatGameId, e.GameLanguageId });

        builder.HasIndex(e => e.GameLanguageId)
            .HasDatabaseName("IX_DatGameLanguages_GameLanguageId");

        builder.HasOne<DatGameEntity>()
            .WithMany()
            .HasForeignKey(e => e.DatGameId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne<GameLanguageEntity>()
            .WithMany()
            .HasForeignKey(e => e.GameLanguageId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
