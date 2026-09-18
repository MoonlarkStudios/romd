using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Romd.Persistence.Entities;

/// <summary>Immutable evidence. Targets deliberately have no foreign key so deletion preserves history.</summary>
public sealed class AdminAuditEventEntity
{
    public long Id { get; set; }
    public Guid ActorId { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
    public string Action { get; set; } = "";
    public string TargetType { get; set; } = "";
    public string TargetId { get; set; } = "";
    public string Changes { get; set; } = "{}";

    public static void Configure(EntityTypeBuilder<AdminAuditEventEntity> builder)
    {
        builder.HasKey(item => item.Id);
        builder.Property(item => item.Action).HasMaxLength(100);
        builder.Property(item => item.TargetType).HasMaxLength(100);
        builder.Property(item => item.TargetId).HasMaxLength(100);
        builder.HasIndex(item => new { item.TargetType, item.TargetId, item.Id });
    }
}
