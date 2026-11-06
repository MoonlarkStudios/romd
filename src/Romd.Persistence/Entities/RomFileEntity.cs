using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Romd.Domain.Hashing;
using Romd.Domain.Source.Rom;

namespace Romd.Persistence.Entities;

/// <summary>Logical bytes only. The preferred output resolves its representation and physical file.</summary>
public sealed class RomFileEntity : EntityBase, ICreatableEntity
{
    public string OriginalFilename { get; set; } = null!;
    public int? PreferredOutputId { get; set; }
    public Sha256 ContentSha256 { get; set; }
    public long ContentSize { get; set; }
    public Sha1 Sha1 { get; set; }
    public Md5 Md5 { get; set; }
    public Crc32 Crc32 { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public Guid CreatedByUserId { get; set; }

    public static void Configure(EntityTypeBuilder<RomFileEntity> builder)
    {
        builder.ToTable("Roms", table => table.ExcludeFromMigrations());
        builder.HasKey(row => row.Id);
        builder.Property(row => row.OriginalFilename).HasMaxLength(500).IsRequired();
        builder.HasIndex(row => row.ContentSha256).IsUnique();
        // SQL owns the composite deferred preferred-output/ROM ownership constraint; readers join it explicitly.
    }

    public RomFile ToDomain(int preferredFileId, RomRepresentationEncoding encoding) => RomFile.Rehydrate(Id, OriginalFilename, preferredFileId,
        ContentSize, Sha1, Md5, Crc32, CreatedAt, ContentSha256, encoding);
}
