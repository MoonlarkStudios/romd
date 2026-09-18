using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Romd.Domain.Hashing;
using Romd.Persistence.Configuration;

namespace Romd.Persistence.Entities;

/// <summary>
///     Canonical file requirement of a <see cref="CatalogReleaseEntity"/>, projected from the
///     representative source DAT. Keyed for upsert by <see cref="FileFingerprint"/> (content),
///     never by <see cref="Name"/> — DAT file names drift across versions even for identical
///     content. Availability is resolved by joining <see cref="Sha1"/> to a stored RomFile.
/// </summary>
public sealed class CatalogReleaseFileEntity : EntityBase
{
    public int CatalogReleaseId { get; set; }

    /// <summary>Content fingerprint: sha1, else md5, else <c>name|size|status</c>.</summary>
    public string FileFingerprint { get; set; } = null!;

    public string Name { get; set; } = null!;
    public long Size { get; set; }
    public Crc32? Crc { get; set; }
    public Md5? Md5 { get; set; }
    public Sha1? Sha1 { get; set; }
    public string? Status { get; set; }

    /// <summary>True when this requirement originates from a DatDisk rather than a DatRom.</summary>
    public bool IsDisk { get; set; }

    public static void Configure(EntityTypeBuilder<CatalogReleaseFileEntity> builder)
    {
        builder.ConfigureEntity("CatalogReleaseFiles");

        builder.Property(f => f.FileFingerprint)
            .IsRequired()
            .HasMaxLength(120);

        builder.Property(f => f.Name)
            .IsRequired()
            .HasMaxLength(500);

        builder.Property(f => f.Crc)
            .HasMaxLength(Crc32.ByteLength);

        builder.Property(f => f.Md5)
            .HasMaxLength(Domain.Hashing.Md5.ByteLength);

        builder.Property(f => f.Sha1)
            .HasMaxLength(Domain.Hashing.Sha1.ByteLength);

        builder.Property(f => f.Status)
            .HasMaxLength(20);

        builder.HasIndex(f => new { f.CatalogReleaseId, f.FileFingerprint })
            .IsUnique()
            .HasDatabaseName("IX_CatalogReleaseFiles_CatalogReleaseId_FileFingerprint");

        builder.HasIndex(f => f.Sha1);

        builder.HasOne<CatalogReleaseEntity>()
            .WithMany()
            .HasForeignKey(f => f.CatalogReleaseId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
