namespace Romd.Admin.Application.Ingestion.Jobs;

public sealed record JobHistoryFilter(
    Guid? OwnerId,
    string? Search,
    string? Outcome,
    string? JobType,
    string Archive,
    DateTimeOffset? From,
    DateTimeOffset? Before,
    DateTimeOffset? CursorCreatedAt,
    Guid? CursorId,
    int Limit);
