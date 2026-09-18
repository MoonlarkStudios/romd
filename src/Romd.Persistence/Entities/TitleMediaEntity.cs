using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Romd.Domain.Catalog;
using Romd.Persistence.Configuration;

namespace Romd.Persistence.Entities;

public sealed class TitleMediaEntity : EntityBase, IEditableEntity
{
    public int TitleId { get; set; }
    public string Type { get; set; } = null!;
    public int FileId { get; set; }
    public string SourceId { get; set; } = null!;
    public bool IsPrimary { get; set; }
    public string ContentType { get; set; } = "image/jpeg";
    public string? SourceUrl { get; set; }
    public string? Attribution { get; set; }
    public string? SourcePageUrl { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public Guid CreatedByUserId { get; set; }
    public DateTimeOffset? UpdatedAt { get; set; }
    public Guid? UpdatedByUserId { get; set; }

    public static void Configure(EntityTypeBuilder<TitleMediaEntity> builder)
    {
        builder.ConfigureEntity("TitleMedia");

        builder.Property(m => m.Type)
            .IsRequired()
            .HasMaxLength(50);

        builder.Property(m => m.SourceId)
            .IsRequired()
            .HasMaxLength(50);

        builder.Property(m => m.ContentType)
            .IsRequired()
            .HasMaxLength(100)
            .HasDefaultValue("image/jpeg");

        builder.Property(m => m.SourceUrl)
            .HasMaxLength(1000);

        builder.Property(m => m.Attribution).HasMaxLength(500);
        builder.Property(m => m.SourcePageUrl).HasMaxLength(1000);

        builder.HasIndex(m => m.TitleId);
        builder.HasIndex(m => m.FileId);
        builder.HasIndex(m => new { m.TitleId, m.Type, m.IsPrimary });
        builder.HasIndex(m => new { m.TitleId, m.Type, m.SourceId })
            .IsUnique().HasFilter("\"SourceId\" <> 'user' AND \"SourceId\" NOT LIKE 'gallery:%'");

        builder.HasOne<FileEntityPersistence>()
            .WithMany()
            .HasForeignKey(m => m.FileId)
            .OnDelete(DeleteBehavior.Cascade);
    }

    public TitleMedia ToDomain()
    {
        if (!Enum.TryParse<MediaType>(Type, out var mediaType))
        {
            mediaType = MediaType.Cover;
        }

        var media = TitleMedia.Rehydrate(
            Id,
            TitleId,
            mediaType,
            FileId,
            SourceId,
            ContentType,
            IsPrimary,
            SourceUrl,
            CreatedAt);
        media.SetAttribution(Attribution, SourcePageUrl);
        return media;
    }

    public static TitleMediaEntity FromDomain(TitleMedia domain)
    {
        return new TitleMediaEntity
        {
            Id = domain.Id,
            TitleId = domain.TitleId,
            Type = domain.Type.ToString(),
            FileId = domain.FileId,
            SourceId = domain.SourceId,
            ContentType = domain.ContentType,
            IsPrimary = domain.IsPrimary,
            SourceUrl = domain.SourceUrl,
            Attribution = domain.Attribution,
            SourcePageUrl = domain.SourcePageUrl,
            CreatedAt = domain.CreatedAt
        };
    }
}
