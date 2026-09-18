using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Romd.Domain.Source.Dat;
using Romd.Persistence.Configuration;

namespace Romd.Persistence.Entities;

/// <summary>
///     Identity anchor for a DAT source. Deliberately minimal: name, platform, and type live on
///     the versions (<see cref="DatFileEntity" />) so replacement can never desynchronize them.
/// </summary>
public sealed class DatSourceEntity : EntityBase, ICreatableEntity
{
    /// <summary>
    ///     The neutral catalog source this DAT source realizes. DAT references catalog,
    ///     never the reverse. One catalog source per DAT source anchor.
    /// </summary>
    public int CatalogSourceId { get; set; }

    /// <summary>Navigation for staged inserts: EF fixes up the FK at flush.</summary>
    public CatalogSourceEntity? CatalogSource { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public Guid CreatedByUserId { get; set; }

    public static void Configure(EntityTypeBuilder<DatSourceEntity> builder)
    {
        builder.ConfigureEntity("DatSources");

        builder.HasIndex(s => s.CatalogSourceId)
            .IsUnique();

        builder.HasOne(s => s.CatalogSource)
            .WithMany()
            .HasForeignKey(s => s.CatalogSourceId)
            .OnDelete(DeleteBehavior.Restrict);
    }

    public static DatSourceEntity FromDomain(DatSource domain) => new()
    {
        Id = domain.Id,
        CreatedAt = domain.CreatedAt
    };
}
