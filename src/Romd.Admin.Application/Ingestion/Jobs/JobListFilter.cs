namespace Romd.Admin.Application.Ingestion.Jobs;

public sealed record JobListFilter(
    Guid? OwnerId,
    string? Search,
    string? Outcome,
    string? JobType,
    string Archive,
    DateTimeOffset? From,
    DateTimeOffset? Before,
    DateTimeOffset? CursorCreatedAt,
    Guid? CursorId,
    int Limit,
    bool IncludeMediaConvergence = true,
    Guid? CorrelationId = null,
    bool Empty = false);
