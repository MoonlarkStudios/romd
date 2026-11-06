using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Romd.Persistence.Identity;

namespace Romd.Persistence.Entities;

public sealed class VfsConnectionEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    public string Name { get; set; } = "";
    public string Format { get; set; } = "stored";
    public byte[] PasswordVerifier { get; set; } = [];
    public string AccountStamp { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset CredentialUpdatedAt { get; set; }
    public DateTimeOffset? LastUsedAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }

    public static void Configure(EntityTypeBuilder<VfsConnectionEntity> builder)
    {
        builder.ToTable("VfsConnections", table => table.HasCheckConstraint("CK_VfsConnections_Format", "\"Format\" IN ('stored', 'original')"));
        builder.HasKey(item => item.Id);
        builder.Property(item => item.Name).HasMaxLength(120);
        builder.Property(item => item.Format).HasMaxLength(20).IsRequired().HasDefaultValue("stored");
        builder.Property(item => item.PasswordVerifier).HasMaxLength(32);
        builder.Property(item => item.AccountStamp).HasMaxLength(256);
        builder.HasIndex(item => new { item.UserId, item.CreatedAt, item.Id });
        builder.HasOne<RomdUser>().WithMany().HasForeignKey(item => item.UserId).OnDelete(DeleteBehavior.Cascade);
    }
}
