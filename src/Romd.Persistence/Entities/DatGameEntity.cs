using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NpgsqlTypes;
using Romd.Domain.Source.Dat;
using Romd.Persistence.Configuration;
using Romd.Persistence.Search;

namespace Romd.Persistence.Entities;

/// <summary>
///     Persistence entity for DatGame.
/// </summary>
public sealed class DatGameEntity : EntityBase, ICreatableEntity
{
    public int DatFileId { get; set; }

    /// <summary>
    ///     Neutral identity this game realizes. Entries are source-scoped, so games from
    ///     successive DAT versions of one source share the entry (upsert on entry key).
    ///     Stamped by the repository at insert; never exposed on the domain model.
    /// </summary>
    public int SourceEntryId { get; set; }

    public string Name { get; set; } = null!;
    public string? Description { get; set; }
    public string SearchDocument { get; set; } = string.Empty;
    public NpgsqlTsVector SearchVector { get; set; } = null!;
    public string? Year { get; set; }
    public string? Manufacturer { get; set; }

    public string? Region { get; set; }
    public string? Language { get; set; }
    public string? Revision { get; set; }
    public string? DevelopmentStatus { get; set; }
    public string? Category { get; set; }

    public string? CloneOf { get; set; }
    public string? RomOf { get; set; }

    public bool IsBios { get; set; }

    // Navigation collections for EF Include()
    public List<DatRomEntity> Roms { get; set; } = [];
    public List<DatDiskEntity> Disks { get; set; } = [];

    public DateTimeOffset CreatedAt { get; set; }
    public Guid CreatedByUserId { get; set; }

    /// <summary>
    ///     Configures the entity for EF Core.
    /// </summary>
    public static void Configure(EntityTypeBuilder<DatGameEntity> builder)
    {
        builder.ConfigureEntity("DatGames");

        builder.Property(g => g.Name)
            .IsRequired()
            .HasMaxLength(500);

        builder.Property(g => g.Description)
            .HasMaxLength(1000);

        SearchDocuments.Configure(builder);

        builder.Property(g => g.Year)
            .HasMaxLength(10);

        builder.Property(g => g.Manufacturer)
            .HasMaxLength(200);

        builder.Property(g => g.CloneOf)
            .HasMaxLength(500);

        builder.Property(g => g.RomOf)
            .HasMaxLength(500);

        builder.Property(g => g.IsBios)
            .IsRequired()
            .HasDefaultValue(false);

        // Index for keyset pagination: games for a DAT ordered by Name, then Id
        builder.HasIndex(g => new { g.DatFileId, g.Name, g.Id });

        // Index for BIOS filtering: supports queries filtering games by IsBios status
        builder.HasIndex(g => new { g.DatFileId, g.IsBios, g.Name, g.Id });

        // Indexes for search filtering
        builder.HasIndex(g => g.Year);
        builder.HasIndex(g => g.Manufacturer);
        builder.HasIndex(g => g.Region);

        // Composite index for Year DESC sorting with keyset pagination
        builder.HasIndex(g => new { g.Year, g.Id });

        // Neutral identity: entries outlive versions, so deletion is restricted while any
        // version's game still references the entry. DAT-graph deletes run games-first.
        builder.HasIndex(g => g.SourceEntryId);
        builder.HasOne<SourceEntryEntity>()
            .WithMany()
            .HasForeignKey(g => g.SourceEntryId)
            .OnDelete(DeleteBehavior.Restrict);

        // Roms relationship
        builder.HasMany(g => g.Roms)
            .WithOne()
            .HasForeignKey(r => r.DatGameId)
            .OnDelete(DeleteBehavior.Cascade);

        // Disks relationship
        builder.HasMany(g => g.Disks)
            .WithOne()
            .HasForeignKey(d => d.DatGameId)
            .OnDelete(DeleteBehavior.Cascade);
    }

    /// <summary>
    ///     Maps this entity to a domain model (including children if loaded).
    /// </summary>
    public DatGame ToDomain()
    {
        return DatGame.Rehydrate(
            Id,
            DatFileId,
            Name,
            Description,
            Year,
            Manufacturer,
            Region,
            Language,
            Revision,
            DevelopmentStatus,
            Category,
            CloneOf,
            RomOf,
            IsBios,
            Roms.Select(r => r.ToDomain()),
            Disks.Select(d => d.ToDomain()));
    }

    /// <summary>
    ///     Creates an entity from a domain model (including children).
    /// </summary>
    public static DatGameEntity FromDomain(DatGame domain)
    {
        return new DatGameEntity
        {
            Id = domain.Id,
            DatFileId = domain.DatFileId,
            Name = domain.Name,
            Description = domain.Description,
            Year = domain.Year,
            Manufacturer = domain.Manufacturer,
            Region = domain.Region,
            Language = domain.Language,
            Revision = domain.Revision,
            DevelopmentStatus = domain.DevelopmentStatus,
            Category = domain.Category,
            CloneOf = domain.CloneOf,
            RomOf = domain.RomOf,
            IsBios = domain.IsBios,
            Roms = domain.Roms.Select(DatRomEntity.FromDomain).ToList(),
            Disks = domain.Disks.Select(DatDiskEntity.FromDomain).ToList()
        };
    }
}
