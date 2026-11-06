using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Romd.Persistence.Configuration;

namespace Romd.Persistence.Entities;

/// <summary>
///     Authoritative source→release provenance: which source entry asserts which canonical
///     release. An entry asserts exactly one release (unique <see cref="SourceEntryId"/>).
///     <see cref="ProviderClaimKey"/> is the exact, provider-local winning claim identity scoped
///     by the globally unique source entry.
///     <see cref="AssertedTitleId"/> snapshots the title the entry linked to at rebuild time
///     (null when the entry is currently unlinked) so title conflicts are reconstructable.
///     Rows are rebuild-time snapshots; live curated truth lives on TitleSourceLinks.
/// </summary>
public sealed class CatalogReleaseSourceEntity : EntityBase
{
    public int CatalogReleaseId { get; set; }
    public int SourceEntryId { get; set; }
    public string ProviderClaimKey { get; set; } = null!;
    public int? AssertedTitleId { get; set; }

    public static void Configure(EntityTypeBuilder<CatalogReleaseSourceEntity> builder)
    {
        builder.ConfigureEntity("CatalogReleaseSources");

        builder.HasIndex(s => s.SourceEntryId)
            .IsUnique()
            .HasDatabaseName("IX_CatalogReleaseSources_SourceEntryId");

        builder.HasIndex(s => s.CatalogReleaseId)
            .HasDatabaseName("IX_CatalogReleaseSources_CatalogReleaseId");

        builder.Property(s => s.ProviderClaimKey)
            .IsRequired()
            .HasMaxLength(200);

        builder.ToTable(table => table.HasCheckConstraint(
            "CK_CatalogReleaseSources_ProviderClaimKey_Length",
            "length(\"ProviderClaimKey\") BETWEEN 1 AND 200"));

        builder.HasOne<CatalogReleaseEntity>()
            .WithMany()
            .HasForeignKey(s => s.CatalogReleaseId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne<SourceEntryEntity>()
            .WithMany()
            .HasForeignKey(s => s.SourceEntryId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
