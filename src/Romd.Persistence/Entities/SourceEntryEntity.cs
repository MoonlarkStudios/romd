using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Romd.Persistence.Configuration;

namespace Romd.Persistence.Entities;

/// <summary>
///     Neutral identity of one claim from one source. Scoped to the source, not to any
///     provider-side version: entries survive DAT replacement via upsert on
///     (CatalogSourceId, EntryKey), which is what lets title curation outlive DAT updates.
///     PlatformId is null until the source is routed; routing stamps it.
///     See docs/decisions/neutral-source-identity.md.
/// </summary>
public sealed class SourceEntryEntity : EntityBase, ICreatableEntity
{
    public int CatalogSourceId { get; set; }
    public string EntryKey { get; set; } = null!;
    public string Name { get; set; } = null!;
    public int? PlatformId { get; set; }

    /// <summary>
    ///     Provider-neutral local-payload assertion maintained by the registered source
    ///     adapters. Title availability composes this fact with effective source links.
    /// </summary>
    public bool HasLocalPayload { get; set; }

    /// <summary>
    ///     Stamp of the last derivation run that claimed this entry. Reconcile's deletion
    ///     pass removes entries of the source whose stamp differs (purely stamp-based —
    ///     providers with payload references use upsert plus provider-side pruning); the
    ///     stamp doubles as ingest audit.
    /// </summary>
    public string? LastReconcileRunId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public Guid CreatedByUserId { get; set; }

    public static void Configure(EntityTypeBuilder<SourceEntryEntity> builder)
    {
        builder.ConfigureEntity("SourceEntries");

        builder.Property(e => e.EntryKey)
            .IsRequired()
            .HasMaxLength(500);

        builder.Property(e => e.Name)
            .IsRequired()
            .HasMaxLength(500);

        builder.Property(e => e.LastReconcileRunId)
            .HasMaxLength(32);

        builder.HasIndex(e => new { e.CatalogSourceId, e.EntryKey })
            .IsUnique();

        builder.HasIndex(e => e.PlatformId);

        builder.HasOne<CatalogSourceEntity>()
            .WithMany()
            .HasForeignKey(e => e.CatalogSourceId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne<PlatformEntity>()
            .WithMany()
            .HasForeignKey(e => e.PlatformId)
            .OnDelete(DeleteBehavior.SetNull);
    }
}
