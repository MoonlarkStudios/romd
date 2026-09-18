using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Romd.Persistence.Entities;

public sealed class TitleProviderMatchStateEntity
{
    public int TitleId { get; set; }
    public string ProviderId { get; set; } = null!;
    public Guid Revision { get; set; } = Guid.NewGuid();
    public string? GameJson { get; set; }
    public bool SuppressAutomaticMatch { get; set; }
    public bool IdentityChanged { get; set; }

    public static void Configure(EntityTypeBuilder<TitleProviderMatchStateEntity> builder)
    {
        builder.ToTable("TitleProviderMatchStates");
        builder.HasKey(x => new { x.TitleId, x.ProviderId });
        builder.Property(x => x.ProviderId).HasMaxLength(50);
        builder.HasOne<TitleEntity>().WithMany().HasForeignKey(x => x.TitleId).OnDelete(DeleteBehavior.Cascade);
    }
}
