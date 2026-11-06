using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Romd.Persistence.Entities;

/// <summary>Current layout archive custody, retained independently of catalog association and logical-ROM lifetime.</summary>
public sealed class CdArchiveOwnerEntity
{
    public int LayoutId { get; set; }
    public int RepresentationId { get; set; }
    public Guid VerificationId { get; set; }
    public Guid PublicationId { get; set; }
    public Guid LayoutRevision { get; set; }
    public int PlatformId { get; set; }
    public int ReleaseId { get; set; }
    public string CatalogFingerprint { get; set; } = "";
    public string EntryPoint { get; set; } = "";
    public long PublishedAtUtcTicks { get; set; }
    public static void Configure(EntityTypeBuilder<CdArchiveOwnerEntity> builder)
    {
        builder.ToTable("CdArchiveOwners", table => table.ExcludeFromMigrations());
        builder.HasKey(row => row.LayoutId);
        builder.Property(row => row.CatalogFingerprint).HasMaxLength(120);
        builder.Property(row => row.EntryPoint).HasMaxLength(240);
        builder.HasIndex(row => row.RepresentationId);
        builder.HasOne<OriginalDiscLayoutEntity>().WithOne().HasForeignKey<CdArchiveOwnerEntity>(row => row.LayoutId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<RomRepresentationEntity>().WithMany().HasForeignKey(row => row.RepresentationId).OnDelete(DeleteBehavior.Restrict);
    }
}
