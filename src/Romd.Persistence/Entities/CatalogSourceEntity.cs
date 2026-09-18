using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Romd.Domain.Catalog;
using Romd.Persistence.Configuration;

namespace Romd.Persistence.Entities;

/// <summary>
///     Neutral registry row: one per source of catalog claims, regardless of provider kind.
///     Provider modules reference their CatalogSource; the catalog never references provider
///     types. Name is null for kinds whose display name is versioned provider-side (DAT names
///     live on <see cref="DatFileEntity" /> versions so replacement can never desynchronize
///     them) and set only for kinds without one.
/// </summary>
public sealed class CatalogSourceEntity : EntityBase, ICreatableEntity
{
    public string Kind { get; set; } = null!;
    public string Status { get; set; } = null!;
    public string? Name { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public Guid CreatedByUserId { get; set; }

    public static void Configure(EntityTypeBuilder<CatalogSourceEntity> builder)
    {
        builder.ConfigureEntity("CatalogSources");

        builder.Property(s => s.Kind)
            .IsRequired()
            .HasMaxLength(50);

        builder.Property(s => s.Status)
            .IsRequired()
            .HasMaxLength(50)
            .HasDefaultValue(nameof(CatalogSourceStatus.Active));

        builder.Property(s => s.Name)
            .HasMaxLength(200);
    }
}
