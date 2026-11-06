using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Romd.Domain.Hashing;

namespace Romd.Persistence.Entities;

/// <summary>An immutable logical projection with a separately mutable catalog association.</summary>
public sealed class RomRepresentationOutputEntity : EntityBase
{
    public int RepresentationId { get; set; }
    public int? MemberOrdinal { get; set; }
    public int? RomId { get; set; }
    public Sha256 ContentSha256 { get; set; }
    public long ContentSize { get; set; }
    public Sha1 Sha1 { get; set; }
    public Md5 Md5 { get; set; }
    public Crc32 Crc32 { get; set; }

    public static void Configure(EntityTypeBuilder<RomRepresentationOutputEntity> builder)
    {
        builder.ToTable("RepresentationOutputs", table => table.ExcludeFromMigrations());
        builder.HasKey(row => row.Id);
        builder.HasOne<RomRepresentationEntity>().WithMany().HasForeignKey(row => row.RepresentationId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<RomFileEntity>().WithMany().HasForeignKey(row => row.RomId).OnDelete(DeleteBehavior.SetNull);
    }
}
