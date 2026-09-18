using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Romd.Persistence.Entities;

/// <summary>Durable, at-least-once metadata work intent committed by the requesting mutation.</summary>
public sealed class MetadataRematerializationRequestEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public int? TitleId { get; set; }
    public int? PlatformId { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset AvailableAtUtc { get; set; }
    public int Attempts { get; set; }

    public static void Configure(EntityTypeBuilder<MetadataRematerializationRequestEntity> builder)
    {
        builder.HasKey(request => request.Id);
        builder.HasIndex(request => new { request.AvailableAtUtc, request.Id });
        // IDs are work arguments: deletion before execution is a no-op.
    }
}
