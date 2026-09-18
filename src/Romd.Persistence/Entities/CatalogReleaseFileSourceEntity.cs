using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Romd.Persistence.Configuration;

namespace Romd.Persistence.Entities;

/// <summary>
///     Authoritative provider-neutral provenance for a canonical release file. Provider-local
///     claim and requirement keys are scoped by the globally unique source entry; requirement
///     kind is part of the identity so providers may reuse a key across requirement kinds.
/// </summary>
public sealed class CatalogReleaseFileSourceEntity : EntityBase
{
    public int CatalogReleaseFileId { get; set; }
    public int SourceEntryId { get; set; }
    public string ProviderClaimKey { get; set; } = null!;
    public string RequirementKind { get; set; } = null!;
    public string ProviderRequirementKey { get; set; } = null!;

    public static void Configure(EntityTypeBuilder<CatalogReleaseFileSourceEntity> builder)
    {
        builder.ConfigureEntity("CatalogReleaseFileSources");

        builder.Property(s => s.ProviderClaimKey)
            .IsRequired()
            .HasMaxLength(200);

        builder.Property(s => s.RequirementKind)
            .IsRequired()
            .HasMaxLength(20);

        builder.Property(s => s.ProviderRequirementKey)
            .IsRequired()
            .HasMaxLength(200);

        builder.ToTable(table =>
        {
            table.HasCheckConstraint(
                "CK_CatalogReleaseFileSources_ProviderKeys_Length",
                "length(\"ProviderClaimKey\") BETWEEN 1 AND 200 " +
                "AND length(\"ProviderRequirementKey\") BETWEEN 1 AND 200");
            table.HasCheckConstraint(
                "CK_CatalogReleaseFileSources_RequirementKind",
                "\"RequirementKind\" IN ('Rom', 'Disk')");
        });

        builder.HasIndex(s => s.CatalogReleaseFileId)
            .HasDatabaseName("IX_CatalogReleaseFileSources_CatalogReleaseFileId");

        builder.HasIndex(s => new
            {
                s.SourceEntryId,
                s.ProviderClaimKey,
                s.RequirementKind,
                s.ProviderRequirementKey
            })
            .IsUnique()
            .HasDatabaseName("UX_CatalogReleaseFileSources_ProviderRequirement");

        builder.HasOne<CatalogReleaseFileEntity>()
            .WithMany()
            .HasForeignKey(s => s.CatalogReleaseFileId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne<SourceEntryEntity>()
            .WithMany()
            .HasForeignKey(s => s.SourceEntryId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
