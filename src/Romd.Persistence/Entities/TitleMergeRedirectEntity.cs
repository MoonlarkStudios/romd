using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Romd.Persistence.Entities;

internal sealed class TitleMergeRedirectEntity
{
    public int SourceTitleId { get; init; }
    public int TargetTitleId { get; init; }

    public static void Configure(EntityTypeBuilder<TitleMergeRedirectEntity> builder)
    {
        builder.ToTable("TitleMergeRedirects", table =>
            table.HasCheckConstraint("CK_TitleMergeRedirects_DistinctTitles", "\"SourceTitleId\" <> \"TargetTitleId\""));
        builder.HasKey(redirect => redirect.SourceTitleId);
        // Only the surviving title has a foreign key. The source title may have been deleted.
        builder.Property(redirect => redirect.SourceTitleId).ValueGeneratedNever();
        builder.HasOne<TitleEntity>().WithMany().HasForeignKey(redirect => redirect.TargetTitleId).OnDelete(DeleteBehavior.Cascade);
    }
}
