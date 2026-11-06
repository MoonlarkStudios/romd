using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Romd.Persistence.Configuration;

namespace Romd.Persistence.Entities;

public sealed class DiscSetEntity : EntityBase
{
    // Historical source/system IDs intentionally have no cascading FK: decisions outlive source removal.
    public int CatalogSourceId { get; set; }
    public int PlatformId { get; set; }
    public int[] TitleIds { get; set; } = [];
    public string ProposalKey { get; set; } = null!;
    public string ProposalJson { get; set; } = null!;
    public string? CorrectionJson { get; set; }
    public string Decision { get; set; } = "proposed";
    public Guid Revision { get; set; } = Guid.NewGuid();
    public Guid? DecidedBy { get; set; }
    public DateTimeOffset? DecidedAt { get; set; }
    public string? Reason { get; set; }

    public static void Configure(EntityTypeBuilder<DiscSetEntity> builder)
    {
        builder.ConfigureEntity("DiscSets");
        builder.Property(x => x.ProposalKey).HasMaxLength(64).IsRequired();
        builder.Property(x => x.ProposalJson).IsRequired();
        builder.Property(x => x.Decision).HasMaxLength(20).IsRequired();
        builder.Property(x => x.Reason).HasMaxLength(1000);
        builder.Property(x => x.Revision).IsConcurrencyToken();
        builder.HasIndex(x => new { x.PlatformId, x.CatalogSourceId, x.ProposalKey }).IsUnique();
        builder.ToTable(t => t.HasCheckConstraint("CK_DiscSets_Decision",
            "\"Decision\" IN ('proposed', 'accepted', 'rejected')"));
    }
}
