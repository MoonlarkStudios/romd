using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Romd.Domain.Hashing;
using Romd.Domain.Source.Rom;
using Romd.Persistence.Configuration;

namespace Romd.Persistence.Entities;

public sealed class RomFileEntity : EntityBase, ICreatableEntity
{
    public string OriginalFilename { get; set; } = null!;
    public int FileId { get; set; }
    public Sha1 Sha1 { get; set; }
    public Md5 Md5 { get; set; }
    public Crc32 Crc32 { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public Guid CreatedByUserId { get; set; }

    public static void Configure(EntityTypeBuilder<RomFileEntity> builder)
    {
        builder.ConfigureEntity("RomFiles");

        builder.Property(r => r.OriginalFilename)
            .IsRequired()
            .HasMaxLength(500);

        builder.Property(r => r.Sha1).IsRequired();
        builder.Property(r => r.Md5).IsRequired();
        builder.Property(r => r.Crc32).IsRequired();

        builder.HasIndex(r => r.Sha1)
            .IsUnique();

        builder.HasIndex(r => r.Md5);
        builder.HasIndex(r => r.Crc32);
        builder.HasIndex(r => r.FileId);

        builder.HasOne<FileEntityPersistence>()
            .WithMany()
            .HasForeignKey(r => r.FileId)
            .OnDelete(DeleteBehavior.Cascade);
    }

    public RomFile ToDomain(long size)
    {
        return RomFile.Rehydrate(
            Id,
            OriginalFilename,
            FileId,
            size,
            Sha1,
            Md5,
            Crc32,
            CreatedAt);
    }

    public static RomFileEntity FromDomain(RomFile domain)
    {
        return new RomFileEntity
        {
            Id = domain.Id,
            OriginalFilename = domain.OriginalFilename,
            FileId = domain.FileId,
            Sha1 = domain.Sha1,
            Md5 = domain.Md5,
            Crc32 = domain.Crc32,
            CreatedAt = domain.ImportedAt
        };
    }
}
