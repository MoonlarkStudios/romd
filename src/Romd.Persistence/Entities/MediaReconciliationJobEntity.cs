using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Romd.Domain.Jobs;
using Romd.Domain.Source.Rom;

namespace Romd.Persistence.Entities;

/// <summary>The common durable dispatch row. Definition, progress and outcomes have separate SQL authority.</summary>
public sealed class MediaReconciliationJobEntity : JobEntity
{
    public const string Type = "media-reconciliation";
    public MediaReconciliationDefinitionEntity Definition { get; set; } = null!;

    public MediaReconciliationJob ToDomain()
    {
        if (Definition is null)
            throw new InvalidDataException("Reconciliation requires its loaded typed definition.");
        var outcome = Definition.Outcome;
        return WithNextAttempt(MediaReconciliationJob.Rehydrate(Id, CorrelationId, Definition.ToRequest(), SourceFilename,
            Definition.OriginSelectionJobId, CreatedByUserId, CreatedAt, PlatformId, Enum.Parse<MediaReconciliationPhase>(Phase),
            outcome is null ? null : Enum.Parse<MediaReconciliationOutcomeKind>(outcome.Kind), outcome?.Code, Definition.ToProgress(),
            HangfireJobId, CurrentItem, JsonSerializer.Deserialize<List<JobError>>(ErrorsJson) ?? throw new InvalidDataException("Missing job error collection."),
            StartedAt, outcome is null ? null : new DateTimeOffset(outcome.CompletedAtUtcTicks, TimeSpan.Zero), IsArchived, ArchivedAt));
    }

    public static MediaReconciliationJobEntity FromDomain(MediaReconciliationJob job, DateTimeOffset updatedAt)
    {
        if (job.IsTerminal)
            throw new InvalidOperationException("New work cannot be manufactured from a terminal snapshot.");
        return new()
        {
            Id = job.Id,
            CorrelationId = job.CorrelationId,
            SourceFilename = job.SourceFilename,
            PlatformId = job.PlatformId,
            JobType = Type,
            Phase = job.Phase,
            HangfireJobId = job.HangfireJobId,
            CurrentItem = job.CurrentItem,
            ErrorsJson = JsonSerializer.Serialize(job.Errors),
            CreatedAt = job.CreatedAt,
            CreatedByUserId = job.CreatedByUserId,
            UpdatedAt = updatedAt,
            StartedAt = job.StartedAt,
            CompletedAt = job.CompletedAt,
            IsArchived = job.IsArchived,
            ArchivedAt = job.ArchivedAt,
            NextAttemptAtUtc = job.NextAttemptAt,
            DeferralCount = job.DeferralCount,
            LastDeferralError = job.LastDeferralError,
            LastDeferralAt = job.LastDeferralAt,
            FirstDeferralAt = job.FirstDeferralAt,
            Definition = MediaReconciliationDefinitionEntity.FromDomain(job)
        };
    }
}

/// <summary>Typed SQL-owned request and selection observations; no serialized persisted domain object.</summary>
public sealed class MediaReconciliationDefinitionEntity
{
    public Guid JobId { get; set; }
    public string RequestMode { get; set; } = null!;
    public string? SubjectKind { get; set; }
    public int? SubjectId { get; set; }
    public Guid? OriginSelectionJobId { get; set; }
    public string? Filter { get; set; }
    public string? KindId { get; set; }
    public int? PlatformId { get; set; }
    public int? UpperRomId { get; set; }
    public int? UpperLayoutId { get; set; }
    public int? UpperRepresentationId { get; set; }
    public int? UpperCatalogFileId { get; set; }
    public long? Limit { get; set; }
    public bool? ConversionRequested { get; set; }
    public bool CancelRequested { get; set; }
    public string? CursorKind { get; set; }
    public int? CursorId { get; set; }
    public long? SelectedCount { get; set; }
    public long? CreatedCount { get; set; }
    public long? AdoptedCount { get; set; }
    public long? SkippedCount { get; set; }
    public long? CompletedCount { get; set; }
    public long? FailedCount { get; set; }
    public bool? EnumerationComplete { get; set; }
    public MediaReconciliationOutcomeEntity? Outcome { get; set; }

    internal MediaReconciliationRequest ToRequest() => RequestMode switch
    {
        "Subject" => new MediaReconciliationRequest.SubjectWork(Subject(SubjectKind!, SubjectId!.Value)),
        "Selection" => new MediaReconciliationRequest.SelectionWork(Filter switch
        {
            "AllSubjects" => new MediaReconciliationFilter.AllSubjects(),
            "RetainedRepresentations" => new MediaReconciliationFilter.RetainedRepresentations(),
            "KindSubjects" => new MediaReconciliationFilter.KindSubjects(KindId!, PlatformId),
            _ => throw new InvalidDataException("Unsupported reconciliation filter.")
        }, new(UpperRomId!.Value, UpperLayoutId!.Value, UpperRepresentationId!.Value, UpperCatalogFileId!.Value), Limit!.Value, ConversionRequested!.Value),
        _ => throw new InvalidDataException("Unsupported reconciliation request mode.")
    };
    internal MediaReconciliationSelectionProgress? ToProgress() => RequestMode == "Subject" ? null : new(
        CursorId is { } id ? Subject(CursorKind!, id) : null, SelectedCount!.Value, CreatedCount!.Value, AdoptedCount!.Value,
        SkippedCount!.Value, CompletedCount!.Value, FailedCount!.Value, EnumerationComplete!.Value);
    internal static MediaReconciliationSubject Subject(string kind, int id) => kind switch
    {
        "LogicalRom" => new MediaReconciliationSubject.LogicalRom(id),
        "OriginalLayout" => new MediaReconciliationSubject.OriginalLayout(id),
        "Representation" => new MediaReconciliationSubject.Representation(id),
        "CatalogCdLayout" => new MediaReconciliationSubject.CatalogCdLayout(id),
        _ => throw new InvalidDataException("Unsupported reconciliation subject namespace.")
    };
    internal static MediaReconciliationDefinitionEntity FromDomain(MediaReconciliationJob job)
    {
        var row = new MediaReconciliationDefinitionEntity { JobId = job.Id, OriginSelectionJobId = job.OriginSelectionJobId };
        if (job.Request is MediaReconciliationRequest.SubjectWork subject)
        {
            row.RequestMode = "Subject";
            row.SubjectKind = subject.Subject.Kind.ToString();
            row.SubjectId = subject.Subject.Id;
        }
        else if (job.Request is MediaReconciliationRequest.SelectionWork selection && job.SelectionProgress is { } progress)
        {
            row.RequestMode = "Selection";
            row.Filter = selection.Filter switch
            {
                MediaReconciliationFilter.AllSubjects => "AllSubjects",
                MediaReconciliationFilter.RetainedRepresentations => "RetainedRepresentations",
                MediaReconciliationFilter.KindSubjects => "KindSubjects",
                _ => throw new InvalidDataException("Unsupported reconciliation selection.")
            };
            if (selection.Filter is MediaReconciliationFilter.KindSubjects kind)
            { row.KindId = kind.Kind; row.PlatformId = kind.PlatformId; }
            row.UpperRomId = selection.Bounds.LogicalRom;
            row.UpperLayoutId = selection.Bounds.OriginalLayout;
            row.UpperRepresentationId = selection.Bounds.Representation;
            row.UpperCatalogFileId = selection.Bounds.CatalogCdLayout;
            row.Limit = selection.Limit;
            row.ConversionRequested = selection.ConversionRequested;
            row.CursorKind = progress.Cursor?.Kind.ToString();
            row.CursorId = progress.Cursor?.Id;
            row.SelectedCount = progress.Selected;
            row.CreatedCount = progress.Created;
            row.AdoptedCount = progress.Adopted;
            row.SkippedCount = progress.Skipped;
            row.CompletedCount = progress.Completed;
            row.FailedCount = progress.Failed;
            row.EnumerationComplete = progress.EnumerationComplete;
        }
        else
            throw new InvalidDataException("Reconciliation requires an exact closed request and selection progress.");
        return row;
    }

    public static void Configure(ModelBuilder model)
    {
        model.Entity<MediaReconciliationDefinitionEntity>(row =>
        {
            row.ToTable("MediaReconciliationJobs", table => table.ExcludeFromMigrations());
            row.HasKey(x => x.JobId);
            row.HasOne<MediaReconciliationJobEntity>().WithOne(job => job.Definition).HasForeignKey<MediaReconciliationDefinitionEntity>(x => x.JobId).OnDelete(DeleteBehavior.Restrict);
            row.HasOne<MediaReconciliationDefinitionEntity>().WithMany().HasForeignKey(x => x.OriginSelectionJobId).OnDelete(DeleteBehavior.Restrict);
            row.Property(x => x.RequestMode).HasMaxLength(16);
            row.Property(x => x.SubjectKind).HasMaxLength(32);
            row.Property(x => x.Filter).HasMaxLength(32);
            row.Property(x => x.KindId).HasMaxLength(64);
            row.Property(x => x.CursorKind).HasMaxLength(32);
        });
        model.Entity<MediaReconciliationOutcomeEntity>(row =>
        {
            row.ToTable("MediaReconciliationOutcomes", table => table.ExcludeFromMigrations());
            row.HasKey(x => x.JobId);
            row.HasOne<MediaReconciliationDefinitionEntity>().WithOne(definition => definition.Outcome).HasForeignKey<MediaReconciliationOutcomeEntity>(x => x.JobId).OnDelete(DeleteBehavior.Restrict);
            row.Property(x => x.Kind).HasMaxLength(32);
            row.Property(x => x.Code).HasMaxLength(128);
        });
    }
}

public sealed class MediaReconciliationOutcomeEntity
{
    public Guid JobId { get; set; }
    public string Kind { get; set; } = null!;
    public string Code { get; set; } = null!;
    public long AcknowledgedVersion { get; set; }
    public Guid ExecutionFenceToken { get; set; }
    public long CompletedAtUtcTicks { get; set; }
}

/// <summary>Only these active dependencies retain CAS files and interpretations; immutable attempt history does not.</summary>
public sealed class MediaReconciliationPinEntity
{
    public Guid JobId { get; set; }
    public int AttemptNumber { get; set; }
    public short Ordinal { get; set; }
    public int FileId { get; set; }
    public int? RepresentationId { get; set; }
    public int? OutputId { get; set; }
    public Guid? VerificationId { get; set; }
    public static void Configure(ModelBuilder model) => model.Entity<MediaReconciliationPinEntity>(row =>
    {
        row.ToTable("MediaReconciliationPins", table => table.ExcludeFromMigrations());
        row.HasKey(x => new { x.JobId, x.AttemptNumber, x.Ordinal });
    });
}
