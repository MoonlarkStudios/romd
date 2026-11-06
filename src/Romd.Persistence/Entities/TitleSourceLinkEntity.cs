using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Romd.Persistence.Entities;

/// <summary>
///     Links a neutral source entry to the canonical title it backs. Successor of
///     GameTitleMappings, re-parented from DAT identity onto <see cref="SourceEntryEntity" />.
///     Entry-level links are the single truth; title-to-sources references are derived reads.
/// </summary>
public sealed class TitleSourceLinkEntity : IEditableEntity
{
    public int SourceEntryId { get; set; }
    public int TitleId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public Guid CreatedByUserId { get; set; }
    public DateTimeOffset? UpdatedAt { get; set; }
    public Guid? UpdatedByUserId { get; set; }

    public static void Configure(EntityTypeBuilder<TitleSourceLinkEntity> builder)
    {
        builder.ToTable("TitleSourceLinks");
        builder.HasKey(l => l.SourceEntryId);

        builder.HasIndex(l => l.TitleId);

        builder.HasOne<SourceEntryEntity>()
            .WithMany()
            .HasForeignKey(l => l.SourceEntryId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne<TitleEntity>()
            .WithMany()
            .HasForeignKey(l => l.TitleId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
