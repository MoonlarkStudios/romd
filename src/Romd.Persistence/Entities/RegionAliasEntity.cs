using Romd.Domain.ReferenceData;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Romd.Persistence.Configuration;

namespace Romd.Persistence.Entities;

public sealed class RegionAliasEntity : EntityBase, ICreatableEntity
{
    public int RegionId { get; set; }
    public string NormalizedAlias { get; set; } = null!;
    // Null means historical provenance is unresolved; it must never silently win a mapping update.
    public ReferenceOwnership? Ownership { get; set; } = ReferenceOwnership.Installation;
    public DateTimeOffset CreatedAt { get; set; }
    public Guid CreatedByUserId { get; set; }

    public static void Configure(EntityTypeBuilder<RegionAliasEntity> builder)
    {
        builder.ConfigureEntity("RegionAliases");
        builder.Property(x => x.Ownership).HasConversion<string>().HasMaxLength(20);

        builder.Property(a => a.NormalizedAlias)
            .IsRequired()
            .HasMaxLength(100);

        builder.HasIndex(a => a.NormalizedAlias)
            .IsUnique()
            .HasDatabaseName("IX_RegionAliases_NormalizedAlias");

        builder.HasIndex(a => a.RegionId)
            .HasDatabaseName("IX_RegionAliases_RegionId");

        builder.HasOne<RegionEntity>()
            .WithMany()
            .HasForeignKey(a => a.RegionId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
