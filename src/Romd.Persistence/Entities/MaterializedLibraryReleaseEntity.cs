using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Romd.Domain.Libraries;
using Romd.Persistence.Configuration;

namespace Romd.Persistence.Entities;

/// <summary>
///     Per-release projection persisted from the domain builder. Eligibility and
///     blocked state are inverse axes for current derivation; playability requires
///     eligibility, ownership, completeness, and no block. Consumers may only read
///     exposed rows and must not recompute exposure.
/// </summary>
public sealed class MaterializedLibraryReleaseEntity : EntityBase
{
    public int LibraryId { get; set; }
    public int TitleId { get; set; }

    /// <summary>
    ///     Stable public release identity. Null only transiently for a representative game whose
    ///     platform catalog has not yet been projected; consumer reads ignore null-id rows.
    /// </summary>
    public int? CatalogReleaseId { get; set; }

    /// <summary>
    ///     Representative source game for diagnostics. Informational only — not lifecycle-owning,
    ///     so a deleted representative does not remove this projection row (rematerialization
    ///     replaces it), which keeps consumer manifests resolvable across DAT replacement.
    /// </summary>
    public int DatGameId { get; set; }
    public int DatFileId { get; set; }
    public int PlatformId { get; set; }
    public bool IsEligible { get; set; }
    public bool IsComplete { get; set; }
    public bool IsOwned { get; set; }
    public bool IsPlayable { get; set; }
    public bool IsBlocked { get; set; }
    public string? BlockReason { get; set; }
    public bool IsExposed { get; set; }
    public string? ExposureReason { get; set; }

    public static void Configure(EntityTypeBuilder<MaterializedLibraryReleaseEntity> builder)
    {
        builder.ConfigureEntity("MaterializedLibraryReleases");

        builder.Property(m => m.BlockReason)
            .HasMaxLength(100);

        builder.Property(m => m.ExposureReason)
            .HasMaxLength(100);

        builder.HasIndex(m => new { m.LibraryId, m.TitleId })
            .HasDatabaseName("IX_MLR_LibraryId_TitleId");

        builder.HasIndex(m => new { m.LibraryId, m.TitleId, m.IsExposed })
            .HasDatabaseName("IX_MLR_LibraryId_TitleId_IsExposed");

        // Consumer release projection and export gate on LibraryId + IsOwned + IsExposed and
        // resolve/scope by TitleId; covers ConsumerReleaseProjection and GetDefaultExportReleaseIds.
        builder.HasIndex(m => new { m.LibraryId, m.IsOwned, m.IsExposed, m.TitleId })
            .HasDatabaseName("IX_MLR_LibraryId_IsOwned_IsExposed_TitleId");

        builder.HasIndex(m => new { m.LibraryId, m.DatGameId })
            .IsUnique()
            .HasDatabaseName("IX_MLR_LibraryId_DatGameId");

        // Consumer reads resolve a release by its stable catalog id within a library.
        builder.HasIndex(m => new { m.LibraryId, m.CatalogReleaseId })
            .HasDatabaseName("IX_MLR_LibraryId_CatalogReleaseId");

        builder.HasIndex(m => new { m.LibraryId, m.IsPlayable })
            .HasDatabaseName("IX_MLR_LibraryId_IsPlayable");

        builder.HasIndex(m => new { m.LibraryId, m.PlatformId })
            .HasDatabaseName("IX_MLR_LibraryId_PlatformId");

        builder.HasOne<LibraryEntity>()
            .WithMany()
            .HasForeignKey(m => m.LibraryId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne<TitleEntity>()
            .WithMany()
            .HasForeignKey(m => m.TitleId)
            .OnDelete(DeleteBehavior.Cascade);

        // No FK on DatGameId: it is an informational representative, not a lifecycle owner. A
        // deleted representative game must not cascade-delete this projection row — rematerialization
        // rebuilds it, and consumer manifests resolve files via CatalogReleaseFile, not DatGameId.
    }

    public static MaterializedLibraryReleaseEntity FromDomain(MaterializedLibraryRelease item)
    {
        return new MaterializedLibraryReleaseEntity
        {
            LibraryId = item.LibraryId,
            TitleId = item.TitleId,
            CatalogReleaseId = item.CatalogReleaseId,
            DatGameId = item.DatGameId,
            DatFileId = item.DatFileId,
            PlatformId = item.PlatformId,
            IsEligible = item.IsEligible,
            IsComplete = item.IsComplete,
            IsOwned = item.IsOwned,
            IsPlayable = item.IsPlayable,
            IsBlocked = item.IsBlocked,
            BlockReason = item.BlockReason,
            IsExposed = item.IsExposed,
            ExposureReason = item.ExposureReason
        };
    }
}
