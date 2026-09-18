using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Romd.Persistence.Identity;

namespace Romd.Persistence.Entities;

public sealed class AccountSessionEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    public string ClientId { get; set; } = "";
    public string Device { get; set; } = "";
    public string AccountStamp { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset LastUsedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
    public Guid? RevokedBy { get; set; }
    public string? RevocationReason { get; set; }

    public static void Configure(EntityTypeBuilder<AccountSessionEntity> builder)
    {
        builder.ToTable("AccountSessions");
        builder.HasKey(item => item.Id);
        builder.Property(item => item.ClientId).HasMaxLength(100);
        builder.Property(item => item.Device).HasMaxLength(120);
        builder.Property(item => item.AccountStamp).HasMaxLength(256);
        builder.Property(item => item.RevocationReason).HasMaxLength(100);
        builder.HasIndex(item => new { item.UserId, item.CreatedAt, item.Id });
        builder.HasOne<RomdUser>().WithMany().HasForeignKey(item => item.UserId).OnDelete(DeleteBehavior.Cascade);
    }
}
