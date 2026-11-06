using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Romd.Domain.Libraries;
using Romd.Persistence.Configuration;

namespace Romd.Persistence.Entities;

/// <summary>
///     Per-title projection persisted from the domain builder. Rows are visible by
///     construction; ownership, playability, counts, and availability are aggregates
///     over eligible release rows.
/// </summary>
public sealed class MaterializedLibraryTitleEntity : EntityBase
{
    public int LibraryId { get; set; }
    public int TitleId { get; set; }
    public int PlatformId { get; set; }
    public string? Genre { get; set; }
    public bool IsVisible { get; set; }
    public bool IsOwned { get; set; }
    public bool IsPlayable { get; set; }
    public int EligibleReleaseCount { get; set; }
    public int PlayableReleaseCount { get; set; }
    public int ExposedReleaseCount { get; set; }
    public string Availability { get; set; } = null!;

    public static void Configure(EntityTypeBuilder<MaterializedLibraryTitleEntity> builder)
    {
        builder.ConfigureEntity("MaterializedLibraryTitles");

        builder.Property(m => m.Genre)
            .HasMaxLength(200);

        builder.Property(m => m.Availability)
            .IsRequired()
            .HasMaxLength(50);

        builder.HasIndex(m => new { m.LibraryId, m.TitleId })
            .IsUnique()
            .HasDatabaseName("IX_MLT_LibraryId_TitleId");

        // Per-platform owned counts and facet GROUP BYs filter LibraryId + IsOwned and read/group
        // PlatformId; this covers them. (No IsOwned+TitleId variant: the unique LibraryId+TitleId
        // index already wins the correlated owned-title gate, with IsOwned as a residual check.)
        builder.HasIndex(m => new { m.LibraryId, m.IsOwned, m.PlatformId })
            .HasDatabaseName("IX_MLT_LibraryId_IsOwned_PlatformId");

        builder.HasIndex(m => new { m.LibraryId, m.IsVisible })
            .HasDatabaseName("IX_MLT_LibraryId_IsVisible");

        builder.HasIndex(m => new { m.LibraryId, m.IsPlayable })
            .HasDatabaseName("IX_MLT_LibraryId_IsPlayable");

        builder.HasIndex(m => new { m.LibraryId, m.PlatformId })
            .HasDatabaseName("IX_MLT_LibraryId_PlatformId");

        builder.HasIndex(m => new { m.LibraryId, m.Genre })
            .HasDatabaseName("IX_MLT_LibraryId_Genre");

        builder.HasOne<LibraryEntity>()
            .WithMany()
            .HasForeignKey(m => m.LibraryId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne<TitleEntity>()
            .WithMany()
            .HasForeignKey(m => m.TitleId)
            .OnDelete(DeleteBehavior.Cascade);
    }

    public static MaterializedLibraryTitleEntity FromDomain(MaterializedLibraryTitle item)
    {
        return new MaterializedLibraryTitleEntity
        {
            LibraryId = item.LibraryId,
            TitleId = item.TitleId,
            PlatformId = item.PlatformId,
            Genre = item.Genre,
            IsVisible = item.IsVisible,
            IsOwned = item.IsOwned,
            IsPlayable = item.IsPlayable,
            EligibleReleaseCount = item.EligibleReleaseCount,
            PlayableReleaseCount = item.PlayableReleaseCount,
            ExposedReleaseCount = item.ExposedReleaseCount,
            Availability = item.Availability.ToString()
        };
    }
}
