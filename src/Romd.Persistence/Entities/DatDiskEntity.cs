using System.ComponentModel.DataAnnotations;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Romd.Domain.Hashing;
using Romd.Domain.Source.Dat;
using Romd.Persistence.Configuration;

namespace Romd.Persistence.Entities;

/// <summary>
///     Persistence entity for DatDisk.
/// </summary>
public sealed class DatDiskEntity : EntityBase, ICreatableEntity
{
    public int DatGameId { get; set; }

    [MaxLength(500)] public string Name { get; set; } = null!;

    public Sha1? Sha1 { get; set; }
    public Md5? Md5 { get; set; }

    [MaxLength(20)] public string? Status { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public Guid CreatedByUserId { get; set; }

    /// <summary>
    ///     Configures the entity for EF Core.
    /// </summary>
    public static void Configure(EntityTypeBuilder<DatDiskEntity> builder)
    {
        builder.ConfigureEntity("DatDisks");

        builder.Property(d => d.Name)
            .IsRequired()
            .HasMaxLength(500);

        builder.Property(d => d.Sha1)
            .HasMaxLength(Domain.Hashing.Sha1.ByteLength);

        builder.Property(d => d.Md5)
            .HasMaxLength(Domain.Hashing.Md5.ByteLength);

        builder.Property(d => d.Status)
            .HasMaxLength(20);

        // Index for disk lookup
        builder.HasIndex(d => d.Sha1);
    }

    /// <summary>
    ///     Maps this entity to a domain model.
    /// </summary>
    public DatDisk ToDomain()
    {
        return DatDisk.Rehydrate(
            Id,
            DatGameId,
            Name,
            Sha1,
            Md5,
            Status);
    }

    /// <summary>
    ///     Creates an entity from a domain model.
    /// </summary>
    public static DatDiskEntity FromDomain(DatDisk domain)
    {
        return new DatDiskEntity
        {
            Id = domain.Id,
            DatGameId = domain.DatGameId,
            Name = domain.Name,
            Sha1 = domain.Sha1,
            Md5 = domain.Md5,
            Status = domain.Status
        };
    }
}
