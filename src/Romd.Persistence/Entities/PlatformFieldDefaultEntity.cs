using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Romd.Persistence.Configuration;

namespace Romd.Persistence.Entities;

public sealed class PlatformFieldDefaultEntity : EntityBase, IEditableEntity
{
    public int PlatformId { get; set; }
    public string FieldName { get; set; } = null!;
    public string SourceId { get; set; } = null!;
    public DateTimeOffset CreatedAt { get; set; }
    public Guid CreatedByUserId { get; set; }
    public DateTimeOffset? UpdatedAt { get; set; }
    public Guid? UpdatedByUserId { get; set; }

    public static void Configure(EntityTypeBuilder<PlatformFieldDefaultEntity> builder)
    {
        builder.ConfigureEntity("PlatformFieldDefaults");

        builder.Property(e => e.FieldName)
            .IsRequired()
            .HasMaxLength(100);

        builder.Property(e => e.SourceId)
            .IsRequired()
            .HasMaxLength(100);

        builder.HasIndex(e => new { e.PlatformId, e.FieldName })
            .IsUnique();

        builder.HasOne<PlatformEntity>()
            .WithMany()
            .HasForeignKey(e => e.PlatformId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
