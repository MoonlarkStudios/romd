using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NpgsqlTypes;
using Romd.Application.Common.Search;

namespace Romd.Persistence.Search;

/// <summary>
///     Persisted full-text search document. The application normalizes name and
///     description with <see cref="SearchTextNormalizer"/> into <c>SearchDocument</c>; PostgreSQL
///     derives the stored <c>tsvector</c> from that column in the same row write, so the index can
///     never lag the text it describes.
/// </summary>
public static class SearchDocuments
{
    public const string TextSearchConfiguration = "simple";
    public const string DocumentColumn = nameof(Entities.TitleEntity.SearchDocument);
    public const string VectorColumn = nameof(Entities.TitleEntity.SearchVector);

    public static string For(string name, string? description) =>
        SearchTextNormalizer.Normalize(string.IsNullOrEmpty(description) ? name : $"{name} {description}");

    public static void Configure<TEntity>(EntityTypeBuilder<TEntity> builder, bool hasEmptyDocumentDefault = true)
        where TEntity : class
    {
        var document = builder.Property<string>(DocumentColumn).IsRequired().HasColumnType("text");
        if (hasEmptyDocumentDefault)
            document.HasDefaultValue(string.Empty);

        builder.Property<NpgsqlTsVector>(VectorColumn)
            .HasColumnType("tsvector")
            .HasComputedColumnSql($"to_tsvector('{TextSearchConfiguration}', \"{DocumentColumn}\")", stored: true);

        builder.HasIndex(VectorColumn).HasMethod("GIN");
    }
}
