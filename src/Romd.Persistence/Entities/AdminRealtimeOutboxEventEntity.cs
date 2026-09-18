using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Romd.Persistence.Realtime;

namespace Romd.Persistence.Entities;

public sealed class AdminRealtimeOutboxEventEntity : EntityBase
{
    public string EventType { get; set; } = null!;
    public string PayloadJson { get; set; } = "{}";
    public int SchemaVersion { get; set; } = AdminRealtimeSchemaVersions.Initial;
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset AvailableAtUtc { get; set; }
    public DateTimeOffset? ProcessedAtUtc { get; set; }
    public int Attempts { get; set; }
    public string? LastError { get; set; }
    public string? ClaimId { get; set; }
    public DateTimeOffset? ClaimedAtUtc { get; set; }

    public static void Configure(EntityTypeBuilder<AdminRealtimeOutboxEventEntity> entity)
    {
        entity.ToTable("AdminRealtimeOutboxEvents");
        entity.HasKey(e => e.Id);

        entity.Property(e => e.EventType)
            .IsRequired()
            .HasMaxLength(100);

        entity.Property(e => e.PayloadJson)
            .IsRequired()
            .HasColumnType("TEXT");

        entity.Property(e => e.SchemaVersion)
            .IsRequired()
            .HasDefaultValue(AdminRealtimeSchemaVersions.Initial);

        entity.Property(e => e.LastError)
            .HasMaxLength(2000);

        entity.Property(e => e.ClaimId)
            .HasMaxLength(64);

        entity.HasIndex(e => new { e.ProcessedAtUtc, e.AvailableAtUtc, e.Id });
        entity.HasIndex(e => e.ClaimId);
        entity.HasIndex(e => e.CreatedAtUtc);
    }
}
