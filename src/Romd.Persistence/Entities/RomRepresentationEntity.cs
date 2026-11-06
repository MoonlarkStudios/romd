using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Romd.Domain.Source.Rom;

namespace Romd.Persistence.Entities;

/// <summary>One physical artifact and encoding. Logical ownership belongs to its outputs.</summary>
public sealed class RomRepresentationEntity : EntityBase
{
    public int FileId { get; set; }
    public RomRepresentationEncoding Encoding { get; set; }

    public static void Configure(EntityTypeBuilder<RomRepresentationEntity> builder)
    {
        builder.ToTable("Representations", table => table.ExcludeFromMigrations());
        builder.HasKey(row => row.Id);
        builder.Property(row => row.Encoding).HasConversion<string>().HasMaxLength(32).IsRequired();
        builder.HasIndex(row => new { row.FileId, row.Encoding });
        builder.HasOne<FileEntityPersistence>().WithMany().HasForeignKey(row => row.FileId).OnDelete(DeleteBehavior.Restrict);
    }
}
