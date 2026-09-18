using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Romd.Domain.Catalog.Ratings;
using Romd.Persistence.Configuration;

namespace Romd.Persistence.Entities;

/// <summary>
///     Effective per-board content rating materialized onto a title. One row per
///     (Title, Board); rows are owned by rematerialization and synced as a set by
///     <see cref="Repositories.TitleRepository" /> — they are deliberately not part
///     of the detached title graph because domain ratings carry no database identity.
/// </summary>
public sealed class TitleContentRatingEntity : EntityBase, IEditableEntity
{
    public int TitleId { get; set; }
    public int Board { get; set; }
    public string Code { get; set; } = null!;
    public int Designation { get; set; }
    public int? MinimumAge { get; set; }
    public string DescriptorsJson { get; set; } = "[]";
    public string? Synopsis { get; set; }
    public string SourceId { get; set; } = null!;
    public string? ExternalRatingId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public Guid CreatedByUserId { get; set; }
    public DateTimeOffset? UpdatedAt { get; set; }
    public Guid? UpdatedByUserId { get; set; }

    public static void Configure(EntityTypeBuilder<TitleContentRatingEntity> builder)
    {
        builder.ConfigureEntity("TitleContentRatings");

        builder.Property(r => r.Code)
            .IsRequired()
            .HasMaxLength(50);

        builder.Property(r => r.DescriptorsJson)
            .IsRequired()
            .HasColumnType("TEXT")
            .HasDefaultValue("[]");

        builder.Property(r => r.Synopsis)
            .HasMaxLength(4000);

        builder.Property(r => r.SourceId)
            .IsRequired()
            .HasMaxLength(50);

        builder.Property(r => r.ExternalRatingId)
            .HasMaxLength(100);

        builder.HasIndex(r => new { r.TitleId, r.Board })
            .IsUnique();

        builder.HasOne<TitleEntity>()
            .WithMany(t => t.ContentRatings)
            .HasForeignKey(r => r.TitleId)
            .OnDelete(DeleteBehavior.Cascade);
    }

    public ContentRating ToDomain()
    {
        IReadOnlyList<string> descriptors = string.IsNullOrWhiteSpace(DescriptorsJson) || DescriptorsJson == "[]"
            ? []
            : JsonSerializer.Deserialize<List<string>>(DescriptorsJson) ?? [];

        return new ContentRating
        {
            Board = (RatingBoard)Board,
            Code = Code,
            Designation = (RatingDesignation)Designation,
            MinimumAge = MinimumAge,
            Descriptors = descriptors,
            Synopsis = Synopsis,
            SourceId = SourceId,
            ExternalRatingId = ExternalRatingId
        };
    }

    public static TitleContentRatingEntity FromDomain(int titleId, ContentRating domain)
    {
        return new TitleContentRatingEntity
        {
            TitleId = titleId,
            Board = (int)domain.Board,
            Code = domain.Code,
            Designation = (int)domain.Designation,
            MinimumAge = domain.MinimumAge,
            DescriptorsJson = domain.Descriptors.Count > 0
                ? JsonSerializer.Serialize(domain.Descriptors)
                : "[]",
            Synopsis = domain.Synopsis,
            SourceId = domain.SourceId,
            ExternalRatingId = domain.ExternalRatingId
        };
    }
}
