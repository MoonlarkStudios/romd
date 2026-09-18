using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Romd.Domain.Catalog;
using Romd.Persistence.Configuration;

namespace Romd.Persistence.Entities;

/// <summary>
///     Persistence entity for <see cref="Bios" />.
/// </summary>
public sealed class BiosEntity : EntityBase, ICreatableEntity
{
    public int PlatformId { get; set; }
    public string Name { get; set; } = null!;
    public string NormalizedName { get; set; } = null!;
    public DateTimeOffset CreatedAt { get; set; }
    public Guid CreatedByUserId { get; set; }

    public static void Configure(EntityTypeBuilder<BiosEntity> builder)
    {
        builder.ConfigureEntity("Bios");

        builder.Property(b => b.Name)
            .IsRequired()
            .HasMaxLength(500);

        builder.Property(b => b.NormalizedName)
            .IsRequired()
            .HasMaxLength(500);

        // One BIOS entry per (platform, normalized name): guarantees idempotent grouping
        // across DAT revisions and backfill re-runs.
        builder.HasIndex(b => new { b.PlatformId, b.NormalizedName })
            .IsUnique();

        builder.HasOne<PlatformEntity>()
            .WithMany()
            .HasForeignKey(b => b.PlatformId)
            .OnDelete(DeleteBehavior.Cascade);
    }

    public Bios ToDomain() =>
        Bios.Rehydrate(Id, PlatformId, Name, NormalizedName, CreatedAt);

    public static BiosEntity FromDomain(Bios domain) =>
        new()
        {
            Id = domain.Id,
            PlatformId = domain.PlatformId,
            Name = domain.Name,
            NormalizedName = domain.NormalizedName,
            CreatedAt = domain.CreatedAt
        };
}
