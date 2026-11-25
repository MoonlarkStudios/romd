using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NpgsqlTypes;
using Romd.Persistence.Search;

namespace Romd.Persistence.Entities;

public sealed class TitleLookupNameEntity
{
    public int TitleId { get; set; }
    public string Name { get; set; } = null!;
    public string SearchDocument { get; set; } = null!;
    public NpgsqlTsVector SearchVector { get; set; } = null!;

    public static void Configure(EntityTypeBuilder<TitleLookupNameEntity> builder)
    {
        builder.ToTable("TitleLookupNames");
        builder.HasKey(name => new { name.TitleId, name.SearchDocument });
        builder.Property(name => name.Name).IsRequired().HasMaxLength(200);
        SearchDocuments.Configure(builder, hasEmptyDocumentDefault: false);
        builder.HasOne<TitleEntity>().WithMany().HasForeignKey(name => name.TitleId).OnDelete(DeleteBehavior.Cascade);
    }
}
