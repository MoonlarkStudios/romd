using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Romd.Persistence.Identity;

namespace Romd.Persistence.Entities;

public sealed class AccountLinkEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    public string TokenHash { get; set; } = "";
    public string Purpose { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? RedeemedAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }

    public static void Configure(EntityTypeBuilder<AccountLinkEntity> builder)
    {
        builder.HasKey(link => link.Id);
        builder.Property(link => link.TokenHash).HasMaxLength(64);
        builder.Property(link => link.Purpose).HasMaxLength(20);
        builder.HasIndex(link => link.TokenHash).IsUnique();
        builder.HasIndex(link => new { link.UserId, link.CreatedAt });
        builder.HasOne<RomdUser>().WithMany().HasForeignKey(link => link.UserId).OnDelete(DeleteBehavior.Cascade);
    }
}
