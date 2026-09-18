using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Romd.Domain.Catalog;
using Romd.Persistence.Configuration;

namespace Romd.Persistence.Entities;

public sealed class TitleExternalIdEntity : EntityBase, IEditableEntity
{
    public int TitleId { get; set; }
    public string Provider { get; set; } = null!;
    public string ExternalId { get; set; } = null!;
    public float MatchConfidence { get; set; }
    public bool IsConfirmed { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public Guid CreatedByUserId { get; set; }
    public DateTimeOffset? UpdatedAt { get; set; }
    public Guid? UpdatedByUserId { get; set; }

    public static void Configure(EntityTypeBuilder<TitleExternalIdEntity> builder)
    {
        builder.ConfigureEntity("TitleExternalIds");

        builder.Property(e => e.Provider)
            .IsRequired()
            .HasMaxLength(50);

        builder.Property(e => e.ExternalId)
            .IsRequired()
            .HasMaxLength(100);

        builder.Property(e => e.IsConfirmed)
            .IsRequired()
            .HasDefaultValue(false);

        builder.HasIndex(e => e.TitleId);
        builder.HasIndex(e => new { e.TitleId, e.Provider })
            .IsUnique();
    }

    public TitleExternalId ToDomain()
    {
        return TitleExternalId.Rehydrate(
            id: Id,
            titleId: TitleId,
            provider: Provider,
            externalId: ExternalId,
            matchConfidence: MatchConfidence,
            isConfirmed: IsConfirmed,
            createdAt: CreatedAt);
    }

    public static TitleExternalIdEntity FromDomain(TitleExternalId domain)
    {
        return new TitleExternalIdEntity
        {
            Id = domain.Id,
            TitleId = domain.TitleId,
            Provider = domain.Provider,
            ExternalId = domain.ExternalId,
            MatchConfidence = domain.MatchConfidence,
            IsConfirmed = domain.IsConfirmed,
            CreatedAt = domain.CreatedAt
        };
    }
}
