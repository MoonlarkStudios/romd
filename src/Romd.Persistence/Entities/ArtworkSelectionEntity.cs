using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Romd.Domain.Catalog;

namespace Romd.Persistence.Entities;

public sealed class ArtworkSelectionEntity
{
    public int TitleId { get; set; }
    public ArtworkRole Role { get; set; }
    public long Revision { get; set; }
    public ArtworkSelectionMode Mode { get; set; }
    public int? PinnedAssetId { get; set; }
    public Guid? PendingRequestId { get; set; }
    public int FocalX { get; set; } = 50;
    public int FocalY { get; set; } = 50;

    public static void Configure(EntityTypeBuilder<ArtworkSelectionEntity> builder)
    {
        builder.ToTable("ArtworkSelections");
        builder.HasKey(s => new { s.TitleId, s.Role });
        builder.Property(s => s.Role).HasConversion<string>().HasMaxLength(20);
        builder.Property(s => s.Mode).HasConversion<string>().HasMaxLength(20);
        builder.Property(s => s.Revision).IsConcurrencyToken();
        builder.Property(s => s.FocalX).HasDefaultValue(50).HasSentinel(-1);
        builder.Property(s => s.FocalY).HasDefaultValue(50).HasSentinel(-1);
        builder.HasOne<TitleEntity>().WithMany().HasForeignKey(s => s.TitleId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<ArtworkAssetEntity>().WithMany()
            .HasForeignKey(s => new { s.PinnedAssetId, s.TitleId, s.Role })
            .HasPrincipalKey(a => new { a.Id, a.TitleId, a.Role }).OnDelete(DeleteBehavior.Restrict);
        builder.ToTable(t =>
        {
            t.HasCheckConstraint("CK_ArtworkSelections_Mode", "(\"Mode\" = 'Automatic' AND \"PinnedAssetId\" IS NULL) OR (\"Mode\" = 'Pinned' AND \"PinnedAssetId\" IS NOT NULL)");
            t.HasCheckConstraint("CK_ArtworkSelections_Revision", "\"Revision\" >= 0");
            t.HasCheckConstraint("CK_ArtworkSelections_FocalPoint", "\"FocalX\" BETWEEN 0 AND 100 AND \"FocalY\" BETWEEN 0 AND 100");
            t.HasCheckConstraint("CK_ArtworkSelections_Role", "\"Role\" IN ('Poster', 'Hero', 'Logo', 'Backdrop')");
        });
    }

    public ArtworkSelection ToDomain() => ArtworkSelection.Rehydrate(TitleId, Role, Revision, Mode, PinnedAssetId, PendingRequestId, FocalX, FocalY);
}
