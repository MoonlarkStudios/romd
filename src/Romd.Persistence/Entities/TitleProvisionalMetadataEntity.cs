using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Romd.Persistence.Entities;

/// <summary>A metadata contribution bound to the exact provisional decision that produced it.</summary>
public sealed class TitleProvisionalMetadataEntity
{
    public int TitleId { get; set; }
    public string ProviderId { get; set; } = null!;
    public string ProviderGameId { get; set; } = null!;
    public Guid MatchRevision { get; set; }
    public string MetadataJson { get; set; } = null!;
    public string RatingsJson { get; set; } = null!;

    internal static void Configure(EntityTypeBuilder<TitleProvisionalMetadataEntity> builder)
    {
        builder.ToTable("TitleProvisionalMetadata", table =>
            table.HasCheckConstraint("CK_TitleProvisionalMetadata_Provider", """("ProviderId" = 'igdb')"""));
        builder.HasKey(value => value.TitleId);
        builder.Property(value => value.TitleId).ValueGeneratedNever();
        builder.Property(value => value.ProviderId).HasMaxLength(50);
        builder.Property(value => value.ProviderGameId).HasMaxLength(100);
        builder.Property(value => value.MetadataJson).HasColumnType("jsonb");
        builder.Property(value => value.RatingsJson).HasColumnType("jsonb");
        builder.HasOne<TitleEntity>().WithOne().HasForeignKey<TitleProvisionalMetadataEntity>(value => value.TitleId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
