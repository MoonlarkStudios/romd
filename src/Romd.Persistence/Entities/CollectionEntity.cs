using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Romd.Domain.Collections;
using Romd.Persistence.Configuration;

namespace Romd.Persistence.Entities;

public sealed class CollectionEntity : EntityBase, IEditableEntity
{
    public string Name { get; set; } = null!;
    public string? Description { get; set; }
    public int? CoverMediaId { get; set; }
    public int? PlatformId { get; set; }
    public bool IsSystem { get; set; }
    public int SortOrder { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public Guid CreatedByUserId { get; set; }
    public DateTimeOffset? UpdatedAt { get; set; }
    public Guid? UpdatedByUserId { get; set; }

    public List<CollectionItemEntity> Items { get; set; } = [];

    public static void Configure(EntityTypeBuilder<CollectionEntity> builder)
    {
        builder.ConfigureEntity("Collections");

        builder.Property(c => c.Name)
            .IsRequired()
            .HasMaxLength(200);

        builder.Property(c => c.Description)
            .HasMaxLength(2000);

        builder.Property(c => c.IsSystem)
            .IsRequired()
            .HasDefaultValue(false);

        builder.Property(c => c.SortOrder)
            .IsRequired()
            .HasDefaultValue(0);

        builder.HasIndex(c => c.Name);
        builder.HasIndex(c => c.PlatformId);
        builder.HasIndex(c => c.SortOrder);

        builder.HasOne<PlatformEntity>()
            .WithMany()
            .HasForeignKey(c => c.PlatformId)
            .OnDelete(DeleteBehavior.SetNull);

        builder.HasOne<TitleMediaEntity>()
            .WithMany()
            .HasForeignKey(c => c.CoverMediaId)
            .OnDelete(DeleteBehavior.SetNull);

        builder.HasMany(c => c.Items)
            .WithOne()
            .HasForeignKey(i => i.CollectionId)
            .OnDelete(DeleteBehavior.Cascade);
    }

    public Collection ToDomain() =>
        Collection.Rehydrate(
            Id, Name, Description, CoverMediaId, PlatformId,
            IsSystem, SortOrder, CreatedAt);

    public Collection ToDomainWithItems() =>
        Collection.Rehydrate(
            Id, Name, Description, CoverMediaId, PlatformId,
            IsSystem, SortOrder, CreatedAt,
            Items.Select(i => i.ToDomain()));

    public static CollectionEntity FromDomain(Collection domain)
    {
        var entity = new CollectionEntity
        {
            Id = domain.Id,
            Name = domain.Name,
            Description = domain.Description,
            CoverMediaId = domain.CoverMediaId,
            PlatformId = domain.PlatformId,
            IsSystem = domain.IsSystem,
            SortOrder = domain.SortOrder,
            CreatedAt = domain.CreatedAt
        };

        entity.Items.AddRange(domain.Items.Select(CollectionItemEntity.FromDomain));

        return entity;
    }
}
