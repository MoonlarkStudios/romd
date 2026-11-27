using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Romd.Persistence.Identity;

namespace Romd.Persistence.Entities;

internal sealed class PlayLaterEntryEntity
{
    public Guid UserId { get; init; }
    public int TitleId { get; init; }
    public DateTimeOffset AddedAt { get; init; }

    public static void Configure(EntityTypeBuilder<PlayLaterEntryEntity> builder)
    {
        builder.ToTable("PlayLaterEntries");
        builder.HasKey(entry => new { entry.UserId, entry.TitleId });
        builder.HasIndex(entry => new { entry.UserId, entry.AddedAt, entry.TitleId }).IsDescending(false, true, true);
        builder.HasOne<RomdUser>().WithMany().HasForeignKey(entry => entry.UserId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<TitleEntity>().WithMany().HasForeignKey(entry => entry.TitleId).OnDelete(DeleteBehavior.Cascade);
    }
}
