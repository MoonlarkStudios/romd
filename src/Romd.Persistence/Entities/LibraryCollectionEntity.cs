using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Romd.Persistence.Entities;

public sealed class LibraryCollectionEntity
{
    public int LibraryId { get; set; }
    public int CollectionId { get; set; }
    public int SortOrder { get; set; }
    public bool IsFeatured { get; set; }

    public static void Configure(EntityTypeBuilder<LibraryCollectionEntity> builder)
    {
        builder.ToTable("LibraryCollections");
        builder.HasKey(x => new { x.LibraryId, x.CollectionId });
        builder.HasOne<LibraryEntity>().WithMany().HasForeignKey(x => x.LibraryId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<CollectionEntity>().WithMany().HasForeignKey(x => x.CollectionId).OnDelete(DeleteBehavior.Cascade);
    }
}
