using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Romd.Domain.Catalog;

namespace Romd.Persistence.Entities;

/// <summary>Installation-wide artwork order, independent of metadata cascade.</summary>
public sealed class ArtworkPreferenceEntity
{
    public ArtworkRole Role { get; set; }
    public string SourceId { get; set; } = null!;
    public int Priority { get; set; }

    public static void Configure(EntityTypeBuilder<ArtworkPreferenceEntity> builder)
    {
        builder.ToTable("ArtworkPreferences");
        builder.HasKey(p => new { p.Role, p.SourceId });
        builder.Property(p => p.Role).HasConversion<string>().HasMaxLength(20);
        builder.Property(p => p.SourceId).HasMaxLength(50);
        builder.HasIndex(p => new { p.Role, p.Priority }).IsUnique();
        builder.ToTable(t =>
        {
            t.HasCheckConstraint("CK_ArtworkPreferences_Priority", "\"Priority\" >= 0");
            t.HasCheckConstraint("CK_ArtworkPreferences_Role", "\"Role\" IN ('Poster', 'Hero', 'Logo', 'Backdrop')");
        });
    }
}
