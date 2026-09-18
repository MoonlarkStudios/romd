using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Romd.Domain.Collections;
using Romd.Persistence.Configuration;

namespace Romd.Persistence.Entities;

public sealed class CollectionItemEntity : EntityBase, ICreatableEntity
{
    public int CollectionId { get; set; }
    public int TitleId { get; set; }
    public int SortOrder { get; set; }
    public string? Note { get; set; }
    public DateTimeOffset AddedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public Guid CreatedByUserId { get; set; }

    public static void Configure(EntityTypeBuilder<CollectionItemEntity> builder)
    {
        builder.ConfigureEntity("CollectionItems");

        builder.Property(i => i.Note)
            .HasMaxLength(500);

        builder.HasIndex(i => new { i.CollectionId, i.TitleId })
            .IsUnique();

        builder.HasIndex(i => i.TitleId);

        builder.HasOne<TitleEntity>()
            .WithMany()
            .HasForeignKey(i => i.TitleId)
            .OnDelete(DeleteBehavior.Cascade);
    }

    public CollectionItem ToDomain() =>
        CollectionItem.Rehydrate(Id, CollectionId, TitleId, SortOrder, Note, AddedAt);

    public static CollectionItemEntity FromDomain(CollectionItem domain) =>
        new()
        {
            Id = domain.Id,
            CollectionId = domain.CollectionId,
            TitleId = domain.TitleId,
            SortOrder = domain.SortOrder,
            Note = domain.Note,
            AddedAt = domain.AddedAt
        };
}
