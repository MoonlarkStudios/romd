using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Romd.Persistence.Entities;

/// <summary>
///     Links a BIOS <see cref="DatGameEntity" /> to its canonical <see cref="BiosEntity" />.
///     The DatGame is the key, so a BIOS game maps to exactly one BIOS entry — the mirror of
///     <see cref="GameTitleMappingEntity" /> for non-BIOS games.
/// </summary>
public sealed class BiosGameMappingEntity : ICreatableEntity
{
    public int DatGameId { get; set; }
    public int BiosId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public Guid CreatedByUserId { get; set; }

    public static void Configure(EntityTypeBuilder<BiosGameMappingEntity> builder)
    {
        builder.ToTable("BiosGameMappings");
        builder.HasKey(m => m.DatGameId);

        builder.HasIndex(m => m.BiosId);

        builder.HasOne<DatGameEntity>()
            .WithMany()
            .HasForeignKey(m => m.DatGameId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne<BiosEntity>()
            .WithMany()
            .HasForeignKey(m => m.BiosId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
