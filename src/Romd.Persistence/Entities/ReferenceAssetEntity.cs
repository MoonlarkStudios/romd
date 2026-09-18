using Romd.Domain.ReferenceData;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Romd.Persistence.Entities;

public sealed class ReferenceAssetEntity
{
    public string Hash { get; set; } = null!;
    public string ContentType { get; set; } = null!;
    public int FileId { get; set; }
    public DateTimeOffset ReservedUntil { get; set; }
    public static void Configure(EntityTypeBuilder<ReferenceAssetEntity> b)
    {
        b.ToTable("ReferenceAssets"); b.HasKey(x => x.Hash); b.Property(x => x.Hash).HasMaxLength(64);
        b.Property(x => x.ContentType).HasMaxLength(64);
        b.HasOne<FileEntityPersistence>().WithMany().HasForeignKey(x => x.FileId).OnDelete(DeleteBehavior.Restrict);
    }
}
