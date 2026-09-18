using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Romd.Domain.Hashing;
using Romd.Domain.Storage;
using Romd.Persistence.Configuration;

namespace Romd.Persistence.Entities;

public sealed class FileEntityPersistence : EntityBase, ICreatableEntity
{
    public Sha256 Sha256 { get; set; }
    public long Size { get; set; }
    public long SizeOnDisk { get; set; }
    public bool IsCompressed { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public Guid CreatedByUserId { get; set; }

    public static void Configure(EntityTypeBuilder<FileEntityPersistence> builder)
    {
        builder.ConfigureEntity("Files");

        builder.Property(f => f.Sha256).IsRequired();

        builder.HasIndex(f => f.Sha256)
            .IsUnique();
    }

    public FileEntity ToDomain()
    {
        return FileEntity.Rehydrate(
            Id,
            Sha256,
            Size,
            SizeOnDisk,
            IsCompressed,
            CreatedAt);
    }

    public static FileEntityPersistence FromDomain(FileEntity domain)
    {
        return new FileEntityPersistence
        {
            Id = domain.Id,
            Sha256 = domain.Sha256,
            Size = domain.Size,
            SizeOnDisk = domain.SizeOnDisk,
            IsCompressed = domain.IsCompressed,
            CreatedAt = domain.CreatedAt
        };
    }
}
