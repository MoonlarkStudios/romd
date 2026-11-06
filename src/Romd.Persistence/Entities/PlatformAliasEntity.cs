using Romd.Domain.ReferenceData;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Romd.Domain.Source.Platform;
using Romd.Persistence.Configuration;

namespace Romd.Persistence.Entities;

public sealed class PlatformAliasEntity : EntityBase, ICreatableEntity
{
    public int PlatformId { get; set; }
    public PlatformAliasType Type { get; set; }
    public string Value { get; set; } = null!;
    public string NormalizedValue { get; set; } = null!;
    public string? Provider { get; set; }
    // Null means historical provenance is unresolved; it must never silently win a mapping update.
    public ReferenceOwnership? Ownership { get; set; } = ReferenceOwnership.Installation;
    public DateTimeOffset CreatedAt { get; set; }
    public Guid CreatedByUserId { get; set; }

    public static void Configure(EntityTypeBuilder<PlatformAliasEntity> builder)
    {
        builder.ConfigureEntity("PlatformAliases");
        builder.Property(x => x.Ownership).HasConversion<string>().HasMaxLength(20);

        builder.Property(a => a.Value)
            .IsRequired()
            .HasMaxLength(200);

        builder.Property(a => a.NormalizedValue)
            .IsRequired()
            .HasMaxLength(200);

        builder.Property(a => a.Provider)
            .HasMaxLength(50);

        // Name aliases route DATs globally — a normalized value may belong to one platform only
        builder.HasIndex(a => a.NormalizedValue)
            .IsUnique()
            .HasFilter($"\"Type\" = {(int)PlatformAliasType.Name}")
            .HasDatabaseName("IX_PlatformAliases_NormalizedValue_Name");

        // One mapping per provider per platform
        builder.HasIndex(a => new { a.PlatformId, a.Provider })
            .IsUnique()
            .HasFilter($"\"Type\" = {(int)PlatformAliasType.ProviderMapping}")
            .HasDatabaseName("IX_PlatformAliases_PlatformId_Provider");

        builder.HasIndex(a => a.PlatformId)
            .HasDatabaseName("IX_PlatformAliases_PlatformId");

        builder.HasOne<PlatformEntity>()
            .WithMany()
            .HasForeignKey(a => a.PlatformId)
            .OnDelete(DeleteBehavior.Cascade);
    }

    public PlatformAlias ToDomain()
    {
        return PlatformAlias.Rehydrate(
            Id,
            PlatformId,
            Type,
            Value,
            NormalizedValue,
            Provider,
            CreatedAt);
    }

    public static PlatformAliasEntity FromDomain(PlatformAlias domain)
    {
        return new PlatformAliasEntity
        {
            Id = domain.Id,
            PlatformId = domain.PlatformId,
            Type = domain.Type,
            Value = domain.Value,
            NormalizedValue = domain.NormalizedValue,
            Provider = domain.Provider,
            CreatedAt = domain.CreatedAt
        };
    }
}
