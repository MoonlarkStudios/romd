using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Romd.Domain.Source.Dat;
using Romd.Persistence.Configuration;

namespace Romd.Persistence.Entities;

public sealed class DatFileEntity : EntityBase, IEditableEntity
{
    public string Name { get; set; } = null!;
    public string Description { get; set; } = null!;
    public string? Version { get; set; }
    public string? Author { get; set; }
    public string? Url { get; set; }
    public string Type { get; set; } = null!;
    public int? PlatformId { get; set; }
    public string OriginalFilename { get; set; } = null!;
    public int FileId { get; set; }
    public int GameCount { get; set; }
    public int RomCount { get; set; }
    public int DiskCount { get; set; }
    public int DatSourceId { get; set; }
    public string Lifecycle { get; set; } = nameof(DatFileLifecycle.Active);
    public DateTimeOffset? SupersededAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public Guid CreatedByUserId { get; set; }
    public DateTimeOffset? UpdatedAt { get; set; }
    public Guid? UpdatedByUserId { get; set; }

    /// <summary>
    ///     Navigation used to stage a new source anchor in the same flush as its first version;
    ///     EF fixes up <see cref="DatSourceId" /> when the source id is generated.
    /// </summary>
    public DatSourceEntity? Source { get; set; }

    public static void Configure(EntityTypeBuilder<DatFileEntity> builder)
    {
        builder.ConfigureEntity("DatFiles");

        builder.Property(d => d.Name)
            .IsRequired()
            .HasMaxLength(500);

        builder.Property(d => d.Description)
            .IsRequired()
            .HasMaxLength(1000);

        builder.Property(d => d.Version)
            .HasMaxLength(100);


        builder.Property(d => d.Url)
            .HasMaxLength(500);

        builder.Property(d => d.Type)
            .IsRequired()
            .HasMaxLength(50);

        builder.Property(d => d.OriginalFilename)
            .IsRequired()
            .HasMaxLength(500);

        builder.Property(d => d.Lifecycle)
            .IsRequired()
            .HasMaxLength(20)
            .HasDefaultValue(nameof(DatFileLifecycle.Active));

        builder.HasIndex(d => d.Name);
        builder.HasIndex(d => d.Type);
        builder.HasIndex(d => d.PlatformId);
        builder.HasIndex(d => d.FileId);

        // Single-active (and single-pending) per source is DB-enforced, not application-promised.
        builder.HasIndex(d => d.DatSourceId, "IX_DatFiles_Active_SourceId")
            .IsUnique()
            .HasFilter($"\"Lifecycle\" = '{nameof(DatFileLifecycle.Active)}'");

        builder.HasIndex(d => d.DatSourceId, "IX_DatFiles_Pending_SourceId")
            .IsUnique()
            .HasFilter($"\"Lifecycle\" = '{nameof(DatFileLifecycle.PendingActivation)}'");

        // Deleting a DAT version must never cascade into its source anchor; last-version
        // cleanup is an explicit repository operation.
        builder.HasOne(d => d.Source)
            .WithMany()
            .HasForeignKey(d => d.DatSourceId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<FileEntityPersistence>()
            .WithMany()
            .HasForeignKey(d => d.FileId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne<PlatformEntity>()
            .WithMany()
            .HasForeignKey(d => d.PlatformId)
            .OnDelete(DeleteBehavior.SetNull);

        builder.HasMany<DatGameEntity>()
            .WithOne()
            .HasForeignKey(g => g.DatFileId)
            .OnDelete(DeleteBehavior.Cascade);
    }

    public DatFile ToDomain()
    {
        if (!Enum.TryParse<DatType>(Type, out var datType))
        {
            datType = DatType.Unknown;
        }

        return DatFile.Rehydrate(
            Id,
            Name,
            Description,
            Version,
            Author,
            Url,
            datType,
            PlatformId,
            OriginalFilename,
            FileId,
            CreatedAt,
            UpdatedAt,
            GameCount,
            RomCount,
            DiskCount,
            DatSourceId,
            Enum.Parse<DatFileLifecycle>(Lifecycle),
            SupersededAt);
    }

    public static DatFileEntity FromDomain(DatFile domain)
    {
        return new DatFileEntity
        {
            Id = domain.Id,
            Name = domain.Name,
            Description = domain.Description,
            Version = domain.Version,
            Author = domain.Author,
            Url = domain.Url,
            Type = domain.Type.ToString(),
            PlatformId = domain.PlatformId,
            OriginalFilename = domain.OriginalFilename,
            FileId = domain.FileId,
            CreatedAt = domain.ImportedAt,
            UpdatedAt = domain.LastUpdatedAt,
            GameCount = domain.GameCount,
            RomCount = domain.RomCount,
            DiskCount = domain.DiskCount,
            DatSourceId = domain.DatSourceId,
            Lifecycle = domain.Lifecycle.ToString(),
            SupersededAt = domain.SupersededAt
        };
    }
}
