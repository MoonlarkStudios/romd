using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Romd.Domain.Catalog;
namespace Romd.Persistence.Entities;

public sealed class ArtworkCompletionItemEntity
{
    public Guid JobId { get; set; }
    public int TitleId { get; set; }
    public string TitleName { get; set; } = null!;
    public bool RequireTracked { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public Guid? FollowUpJobId { get; set; }
    /// <summary>Recorded metadata execution; null is unknown history from before outcomes were recorded.</summary>
    public MetadataExecutionOutcome? MetadataOutcome { get; set; }
    public static void Configure(EntityTypeBuilder<ArtworkCompletionItemEntity> b)
    {
        b.ToTable("ArtworkCompletionItems");
        b.HasKey(x => new { x.JobId, x.TitleId });
        b.Property(x => x.MetadataOutcome).HasConversion<string>().HasMaxLength(20);
        b.HasIndex(x => new { x.JobId, x.CompletedAt, x.TitleId });
        b.HasIndex(x => new { x.TitleId, x.CompletedAt });
        b.HasOne<ArtworkCompletionJobEntity>().WithMany().HasForeignKey(x => x.JobId).OnDelete(DeleteBehavior.Cascade);
        // Preserve run history after a title is removed.
    }
}
public sealed class ArtworkCompletionResultEntity
{
    public Guid JobId { get; set; }
    public int TitleId { get; set; }
    public ArtworkRole Role { get; set; }
    public ArtworkCompletionOutcome Outcome { get; set; }
    public static void Configure(EntityTypeBuilder<ArtworkCompletionResultEntity> b)
    {
        b.ToTable("ArtworkCompletionResults");
        b.HasKey(x => new { x.JobId, x.TitleId, x.Role });
        b.Property(x => x.Role).HasConversion<string>().HasMaxLength(20);
        b.Property(x => x.Outcome).HasConversion<string>().HasMaxLength(30);
        b.HasOne<ArtworkCompletionItemEntity>().WithMany().HasForeignKey(x => new { x.JobId, x.TitleId }).OnDelete(DeleteBehavior.Cascade);
    }
}
