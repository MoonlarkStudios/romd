using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Romd.Persistence.Entities;

/// <summary>One durable dispatch identity for an application job, committed with its acceptance.</summary>
public sealed class JobDispatchEntity
{
    public Guid JobId { get; set; }
    public string JobType { get; set; } = null!;
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset AvailableAtUtc { get; set; }
    public Guid? ClaimToken { get; set; }
    public DateTimeOffset? LeaseExpiresAtUtc { get; set; }
    public DateTimeOffset? DeliveredAtUtc { get; set; }
    public string? HangfireJobId { get; set; }
    public int AttemptCount { get; set; }
    public string? LastError { get; set; }

    public static void Configure(EntityTypeBuilder<JobDispatchEntity> builder)
    {
        builder.HasKey(dispatch => dispatch.JobId);
        builder.Property(dispatch => dispatch.JobType).HasMaxLength(50);
        builder.Property(dispatch => dispatch.HangfireJobId).HasMaxLength(100);
        builder.Property(dispatch => dispatch.LastError).HasMaxLength(1000);
        builder.HasOne<JobEntity>().WithOne().HasForeignKey<JobDispatchEntity>(dispatch => dispatch.JobId);
        builder.HasIndex(dispatch => new { dispatch.DeliveredAtUtc, dispatch.AvailableAtUtc, dispatch.JobId });
    }
}
