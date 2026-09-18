using System.Text.Json;
using Romd.Domain.Jobs;

namespace Romd.Persistence.Entities;

public sealed class ExportJobEntity : JobEntity
{
    public string? ExportScopeKind { get; set; }
    public int? LibraryId { get; set; }
    public long? AuthorizedMaterializationGeneration { get; set; }
    public int TotalTitles { get; set; }
    public int ProcessedTitles { get; set; }
    public int TotalFiles { get; set; }
    public int ProcessedFiles { get; set; }
    public int SkippedFiles { get; set; }
    public string? ExportPath { get; set; }

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

    public ExportJob ToDomain()
    {
        if (!Enum.TryParse<ExportPhase>(Phase, out var phase))
        {
            phase = ExportPhase.Pending;
        }

        Romd.Domain.Jobs.ExportScopeKind? scopeKind = ExportScopeKind is null
            ? null
            : Enum.TryParse<Romd.Domain.Jobs.ExportScopeKind>(ExportScopeKind, ignoreCase: false, out var parsedScopeKind)
                ? parsedScopeKind
                : Romd.Domain.Jobs.ExportScopeKind.Invalid;

        return ExportJob.Rehydrate(
            id: Id,
            correlationId: CorrelationId,
            sourceFilename: SourceFilename,
            platformId: PlatformId,
            phase: phase,
            hangfireJobId: HangfireJobId,
            scopeKind: scopeKind,
            libraryId: LibraryId,
            authorizedMaterializationGeneration: AuthorizedMaterializationGeneration,
            totalTitles: TotalTitles,
            processedTitles: ProcessedTitles,
            totalFiles: TotalFiles,
            processedFiles: ProcessedFiles,
            skippedFiles: SkippedFiles,
            exportPath: ExportPath,
            currentItem: CurrentItem,
            errors: GetErrors(),
            createdAt: CreatedAt,
            startedAt: StartedAt,
            completedAt: CompletedAt,
            isArchived: IsArchived,
            archivedAt: ArchivedAt,
            createdByUserId: CreatedByUserId);
    }

    public static ExportJobEntity FromDomain(ExportJob domain, DateTimeOffset? updatedAt = null)
    {
        return new ExportJobEntity
        {
            Id = domain.Id,
            CorrelationId = domain.CorrelationId,
            SourceFilename = domain.SourceFilename,
            PlatformId = domain.PlatformId,
            Phase = domain.Phase,
            HangfireJobId = domain.HangfireJobId,
            ExportScopeKind = domain.ScopeKind?.ToString(),
            LibraryId = domain.LibraryId,
            AuthorizedMaterializationGeneration = domain.AuthorizedMaterializationGeneration,
            TotalTitles = domain.TotalTitles,
            ProcessedTitles = domain.ProcessedTitles,
            TotalFiles = domain.TotalFiles,
            ProcessedFiles = domain.ProcessedFiles,
            SkippedFiles = domain.SkippedFiles,
            ExportPath = domain.ExportPath,
            CurrentItem = domain.CurrentItem,
            ErrorsJson = JsonSerializer.Serialize(domain.Errors.ToList()),
            CreatedAt = domain.CreatedAt,
            StartedAt = domain.StartedAt,
            CompletedAt = domain.CompletedAt,
            IsArchived = domain.IsArchived,
            ArchivedAt = domain.ArchivedAt,
            CreatedByUserId = domain.CreatedByUserId,
            UpdatedAt = updatedAt ?? domain.CreatedAt,
            JobType = "export"
        };
    }
}
