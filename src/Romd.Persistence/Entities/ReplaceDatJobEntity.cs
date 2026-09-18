using System.Text.Json;
using Romd.Admin.Application.Diagnostics;
using Romd.Domain.Jobs;

namespace Romd.Persistence.Entities;

public sealed class ReplaceDatJobEntity : JobEntity
{
    public int ExistingDatId { get; set; }
    public int? NewDatId { get; set; }

    public IReadOnlyList<JobError> GetErrors()
    {
        try
        {
            return JsonSerializer.Deserialize<List<JobError>>(ErrorsJson) ?? [];
        }
        catch
        {
            return [];
        }
    }

    public ReplaceDatJob ToDomain()
    {
        if (!Enum.TryParse<ReplaceDatPhase>(Phase, out var phase))
        {
            phase = ReplaceDatPhase.Pending;
        }

        return ReplaceDatJob.Rehydrate(
            id: Id,
            correlationId: CorrelationId,
            existingDatId: ExistingDatId,
            newDatId: NewDatId,
            sourceFilename: SourceFilename,
            platformId: PlatformId,
            phase: phase,
            hangfireJobId: HangfireJobId,
            currentItem: CurrentItem,
            errors: GetErrors(),
            createdAt: CreatedAt,
            startedAt: StartedAt,
            completedAt: CompletedAt,
            isArchived: IsArchived,
            archivedAt: ArchivedAt,
            createdByUserId: CreatedByUserId);
    }

    public static ReplaceDatJobEntity FromDomain(ReplaceDatJob domain, DateTimeOffset? updatedAt = null)
    {
        string? attemptError = domain.Errors.LastOrDefault(error => error.Item == "attempt")?.Message;
        bool attemptErrorTruncated = attemptError?.Length > OperationalDiagnosticsPolicy.AttemptErrorLimit;

        return new ReplaceDatJobEntity
        {
            Id = domain.Id,
            CorrelationId = domain.CorrelationId,
            ExistingDatId = domain.ExistingDatId,
            NewDatId = domain.NewDatId,
            SourceFilename = domain.SourceFilename,
            PlatformId = domain.PlatformId,
            Phase = domain.Phase,
            HangfireJobId = domain.HangfireJobId,
            CurrentItem = domain.CurrentItem,
            ErrorsJson = JsonSerializer.Serialize(domain.Errors.ToList()),
            LastAttemptError = attemptError is null
                ? null
                : attemptError[..Math.Min(attemptError.Length, OperationalDiagnosticsPolicy.AttemptErrorLimit)],
            LastAttemptErrorTruncated = attemptErrorTruncated,
            CreatedAt = domain.CreatedAt,
            StartedAt = domain.StartedAt,
            CompletedAt = domain.CompletedAt,
            IsArchived = domain.IsArchived,
            ArchivedAt = domain.ArchivedAt,
            CreatedByUserId = domain.CreatedByUserId,
            UpdatedAt = updatedAt ?? domain.CreatedAt,
            JobType = "replace_dat"
        };
    }
}
