using Romd.Domain.ReferenceData;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Romd.Persistence.Entities;

public sealed class ReferenceAssetOwnerEntity
{
    public string Kind { get; set; } = null!;
    public string Key { get; set; } = null!;
    public string Slot { get; set; } = null!;
    public string Hash { get; set; } = null!;
    public static void Configure(EntityTypeBuilder<ReferenceAssetOwnerEntity> b)
    {
        b.ToTable("ReferenceAssetOwners"); b.HasKey(x => new { x.Kind, x.Key, x.Slot });
        b.Property(x => x.Kind).HasMaxLength(32); b.Property(x => x.Key).HasMaxLength(128);
        b.Property(x => x.Slot).HasMaxLength(16); b.Property(x => x.Hash).HasMaxLength(64);
        b.HasOne<ReferenceAssetEntity>().WithMany().HasForeignKey(x => x.Hash).OnDelete(DeleteBehavior.Restrict);
    }
}
