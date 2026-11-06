using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Romd.Domain.Activity;
using Romd.Persistence.Identity;

namespace Romd.Persistence.Entities;

public sealed class PlaySessionEntity
{
    public Guid UserId { get; set; }
    public Guid SessionId { get; set; }
    public string ClientId { get; set; } = "";
    public int TitleId { get; set; }
    public int ReleaseId { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? EndedAt { get; set; }
    public int? ActiveDurationSeconds { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    public PlaySession ToDomain() => new(
        UserId, SessionId, ClientId, TitleId, ReleaseId, StartedAt, EndedAt,
        ActiveDurationSeconds, CreatedAt, UpdatedAt);

    public static PlaySessionEntity FromDomain(PlaySession session) => new()
    {
        UserId = session.UserId,
        SessionId = session.SessionId,
        ClientId = session.ClientId,
        TitleId = session.TitleId,
        ReleaseId = session.ReleaseId,
        StartedAt = session.StartedAt,
        EndedAt = session.EndedAt,
        ActiveDurationSeconds = session.ActiveDurationSeconds,
        CreatedAt = session.CreatedAt,
        UpdatedAt = session.UpdatedAt
    };

    public static void Configure(EntityTypeBuilder<PlaySessionEntity> builder)
    {
        builder.ToTable("PlaySessions", table =>
        {
            table.HasCheckConstraint("CK_PlaySessions_End", "\"EndedAt\" IS NULL OR \"EndedAt\" >= \"StartedAt\"");
            table.HasCheckConstraint(
                "CK_PlaySessions_ActiveDuration",
                $"\"ActiveDurationSeconds\" IS NULL OR (\"ActiveDurationSeconds\" >= 0 AND \"ActiveDurationSeconds\" <= {PlaySession.MaximumActiveDurationSeconds})");
        });
        builder.HasKey(item => new { item.UserId, item.SessionId });
        builder.Property(item => item.ClientId).IsRequired().HasMaxLength(200);
        builder.HasIndex(item => new { item.UserId, item.StartedAt, item.SessionId });
        builder.HasIndex(item => new { item.UserId, item.TitleId, item.StartedAt });
        builder.HasOne<RomdUser>().WithMany().HasForeignKey(item => item.UserId).OnDelete(DeleteBehavior.Cascade);
    }
}
