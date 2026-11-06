using Microsoft.EntityFrameworkCore;

namespace Romd.Persistence.Entities;

public sealed class MediaReconciliationSubjectEntity
{
    public string SubjectKind { get; set; } = null!;
    public int SubjectId { get; set; }
    public Guid LatestJobId { get; set; }
    public long RequestedVersion { get; set; }
    public long AcknowledgedVersion { get; set; }
    public bool ConversionRequested { get; set; }
}
public sealed class MediaReconciliationRequestEntity
{
    public Guid Id { get; set; }
    public Guid JobId { get; set; }
    public Guid? ActorId { get; set; }
    public Guid? RetryOfJobId { get; set; }
    public string Cause { get; set; } = null!;
    public long RequestedVersion { get; set; }
    public bool ConversionRequested { get; set; }
    public Guid? OriginSelectionJobId { get; set; }
    public long CreatedAtUtcTicks { get; set; }
    public static void Configure(ModelBuilder model)
    {
        model.Entity<MediaReconciliationSubjectEntity>(row =>
        {
            row.ToTable("MediaReconciliationSubjects", table => table.ExcludeFromMigrations());
            row.HasKey(x => new { x.SubjectKind, x.SubjectId });
            row.Property(x => x.SubjectKind).HasMaxLength(32);
            row.HasOne<MediaReconciliationDefinitionEntity>().WithMany().HasForeignKey(x => x.LatestJobId).OnDelete(DeleteBehavior.Restrict);
            row.HasIndex(x => x.LatestJobId).IsUnique();
        });
        model.Entity<MediaReconciliationRequestEntity>(row =>
        {
            row.ToTable("MediaReconciliationRequests", table => table.ExcludeFromMigrations());
            row.HasKey(x => x.Id);
            row.Property(x => x.Cause).HasMaxLength(32);
            row.HasOne<MediaReconciliationDefinitionEntity>().WithMany().HasForeignKey(x => x.JobId).OnDelete(DeleteBehavior.Restrict);
            row.HasOne<MediaReconciliationDefinitionEntity>().WithMany().HasForeignKey(x => x.OriginSelectionJobId).OnDelete(DeleteBehavior.Restrict);
            row.HasOne<MediaReconciliationDefinitionEntity>().WithMany().HasForeignKey(x => x.RetryOfJobId).OnDelete(DeleteBehavior.Restrict);
        });
    }
}
