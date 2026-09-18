using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Romd.Persistence.Configuration;

namespace Romd.Persistence.Entities;

/// <summary>A subscription identity survives replacement of its bound DAT version.</summary>
public sealed class DatSubscriptionEntity : EntityBase
{
    public int? DatSourceId { get; set; }
    // Keep the selected identity even if a local platform is later removed.
    public int? PlatformId { get; set; }
    public string? SystemId { get; set; }
    public string? ExpectedName { get; set; }
    public string CatalogId { get; set; } = "redump/psx/discs";
    public string State { get; set; } = "NotChecked";
    public DateTimeOffset? LastCheckedAt { get; set; }
    public DateTimeOffset? NextCheckAt { get; set; }
    public int ConsecutiveCheckFailures { get; set; }
    public string? Message { get; set; }
    public int? CandidateFileId { get; set; }
    public string? CandidateSha256 { get; set; }
    public string? ActiveSha256 { get; set; }
    public Guid? JobId { get; set; }

    public static void Configure(EntityTypeBuilder<DatSubscriptionEntity> builder)
    {
        builder.ConfigureEntity("DatSubscriptions");
        builder.Property(x => x.SystemId).HasMaxLength(50);
        builder.Property(x => x.ExpectedName).HasMaxLength(200);
        builder.HasIndex(x => x.CatalogId).IsUnique().HasFilter("\"DatSourceId\" IS NULL");
        builder.Property(x => x.CatalogId).HasMaxLength(100).IsRequired();
        builder.Property(x => x.State).HasMaxLength(32).IsRequired();
        builder.Property(x => x.Message).HasMaxLength(500);
        builder.Property(x => x.CandidateSha256).HasMaxLength(64);
        builder.Property(x => x.ActiveSha256).HasMaxLength(64);
        builder.HasIndex(x => x.DatSourceId).IsUnique();
        builder.HasOne<DatSourceEntity>().WithMany().HasForeignKey(x => x.DatSourceId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<FileEntityPersistence>().WithMany().HasForeignKey(x => x.CandidateFileId).OnDelete(DeleteBehavior.Restrict);
    }
}
