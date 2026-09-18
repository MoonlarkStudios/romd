using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Romd.Domain.Hashing;
using Romd.Domain.Source.Dat;
using Romd.Persistence.Configuration;

namespace Romd.Persistence.Entities;

public sealed class DatRomEntity : EntityBase, ICreatableEntity
{
    public int DatGameId { get; set; }
    public string Name { get; set; } = null!;
    public long Size { get; set; }
    public Crc32? Crc { get; set; }
    public Md5? Md5 { get; set; }
    public Sha1? Sha1 { get; set; }
    public string? Status { get; set; }
    public string? Serial { get; set; }
    public int? RomFileId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public Guid CreatedByUserId { get; set; }

    public static void Configure(EntityTypeBuilder<DatRomEntity> builder)
    {
        builder.ConfigureEntity("DatRoms");

        builder.Property(r => r.Name)
            .IsRequired()
            .HasMaxLength(500);

        builder.Property(r => r.Status)
            .HasMaxLength(20);

        builder.Property(r => r.Serial)
            .HasMaxLength(100);

        builder.HasOne<RomFileEntity>()
            .WithMany()
            .HasForeignKey(r => r.RomFileId)
            .OnDelete(DeleteBehavior.SetNull);

        builder.HasIndex(r => r.Sha1);
        builder.HasIndex(r => r.Md5);
        builder.HasIndex(r => r.Crc);
        builder.HasIndex(r => r.RomFileId);
        builder.HasIndex(r => new { r.DatGameId, r.Name });

        // Answers "does this game have an owned ROM?" (RomFileId != null) index-only for the
        // ownership/coverage EXISTS subqueries in RomRepository and SearchRepository.
        builder.HasIndex(r => new { r.DatGameId, r.RomFileId });
    }

    public DatRom ToDomain()
    {
        return DatRom.Rehydrate(
            Id,
            DatGameId,
            Name,
            Size,
            Crc,
            Md5,
            Sha1,
            Status,
            Serial,
            RomFileId);
    }

    public static DatRomEntity FromDomain(DatRom domain)
    {
        return new DatRomEntity
        {
            Id = domain.Id,
            DatGameId = domain.DatGameId,
            Name = domain.Name,
            Size = domain.Size,
            Crc = domain.Crc,
            Md5 = domain.Md5,
            Sha1 = domain.Sha1,
            Status = domain.Status,
            Serial = domain.Serial,
            RomFileId = domain.RomFileId
        };
    }
}
