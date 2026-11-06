using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Romd.Domain.Hashing;
using Romd.Persistence.Configuration;

namespace Romd.Persistence.Entities;

/// <summary>
///     Stable, platform-scoped canonical release derived from DAT file requirements.
///     The release is the public download/read unit; its identity is the content
///     <see cref="Fingerprint"/>, not any source DAT row.
///     SHA-based fingerprints reconnect across DAT replacement (the row survives a source
///     deletion and a replacement DAT re-asserts the same fingerprint); fallback
///     fingerprints (no SHA available) are aggressively pruned and are not guaranteed to
///     reconnect. Display metadata and file requirements are projected from the
///     representative source by <c>CatalogProjectionService</c>.
/// </summary>
public sealed class CatalogReleaseEntity : EntityBase, ICreatableEntity
{
    public int PlatformId { get; set; }
    public int CatalogTitleId { get; set; }
    public string Fingerprint { get; set; } = null!;
    public Sha1? PrimarySha1 { get; set; }

    public string Name { get; set; } = null!;
    public string? Region { get; set; }
    public string? Language { get; set; }
    public string? Revision { get; set; }

    public int SourceCount { get; set; }
    public bool HasTitleConflict { get; set; }

    /// <summary>
    ///     Total byte size and file count of this release's files, maintained by
    ///     <c>CatalogProjectionService</c> so consumer/export reads never re-sum
    ///     <see cref="CatalogReleaseFileEntity"/> rows per release.
    /// </summary>
    public long SizeBytes { get; set; }
    public int FileCount { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public Guid CreatedByUserId { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    public static void Configure(EntityTypeBuilder<CatalogReleaseEntity> builder)
    {
        builder.ConfigureEntity("CatalogReleases");

        builder.Property(r => r.Fingerprint)
            .IsRequired()
            .HasMaxLength(120);

        builder.Property(r => r.PrimarySha1)
            .HasMaxLength(Sha1.ByteLength);

        builder.Property(r => r.Name)
            .IsRequired()
            .HasMaxLength(500);

        builder.Property(r => r.Region)
            .HasMaxLength(200);

        builder.Property(r => r.Language)
            .HasMaxLength(200);

        builder.Property(r => r.Revision)
            .HasMaxLength(200);

        builder.HasIndex(r => new { r.PlatformId, r.Fingerprint })
            .IsUnique()
            .HasDatabaseName("IX_CatalogReleases_PlatformId_Fingerprint");

        builder.HasIndex(r => new { r.PlatformId, r.PrimarySha1 })
            .HasDatabaseName("IX_CatalogReleases_PlatformId_PrimarySha1");

        builder.HasIndex(r => r.CatalogTitleId)
            .HasDatabaseName("IX_CatalogReleases_CatalogTitleId");

        builder.HasOne<PlatformEntity>()
            .WithMany()
            .HasForeignKey(r => r.PlatformId)
            .OnDelete(DeleteBehavior.Cascade);

        // The rebuild owns release lifecycle; do not cascade-delete releases when a title
        // is pruned (titles with user state are retained as UserOnly).
        builder.HasOne<TitleEntity>()
            .WithMany()
            .HasForeignKey(r => r.CatalogTitleId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
