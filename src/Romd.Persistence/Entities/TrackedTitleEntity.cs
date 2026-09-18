using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Romd.Domain.Catalog;
using Romd.Persistence.Configuration;

namespace Romd.Persistence.Entities;

public sealed class TrackedTitleEntity : EntityBase
{
    public int TitleId { get; set; }
    public int? PinnedCatalogReleaseId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    public static void Configure(EntityTypeBuilder<TrackedTitleEntity> builder)
    {
        builder.ConfigureEntity("TrackedTitles");

        builder.HasIndex(t => t.TitleId)
            .IsUnique();

        builder.HasIndex(t => t.PinnedCatalogReleaseId);

        builder.HasOne<TitleEntity>()
            .WithMany()
            .HasForeignKey(t => t.TitleId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<CatalogReleaseEntity>()
            .WithMany()
            .HasForeignKey(t => t.PinnedCatalogReleaseId)
            .OnDelete(DeleteBehavior.SetNull);
    }

    public TrackedTitle ToDomain() =>
        TrackedTitle.Rehydrate(Id, TitleId, PinnedCatalogReleaseId, CreatedAt, UpdatedAt);

    public static TrackedTitleEntity FromDomain(TrackedTitle domain) => new()
    {
        Id = domain.Id,
        TitleId = domain.TitleId,
        PinnedCatalogReleaseId = domain.PinnedCatalogReleaseId,
        CreatedAt = domain.CreatedAt,
        UpdatedAt = domain.UpdatedAt
    };
}
