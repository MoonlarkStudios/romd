namespace Romd.Contracts.Management.Models;

/// <summary>One media subject or a finite selection of subjects. Outcomes come from the committed reconciliation journal.</summary>
public sealed record MediaReconciliationJobDto : JobDto
{
    public required string RequestMode { get; init; }
    public MediaReconciliationSubjectDto? Subject { get; init; }
    public MediaReconciliationSelectionDto? Selection { get; init; }
    public Guid? OriginSelectionJobId { get; init; }
    public string? Outcome { get; init; }
    public string? OutcomeCode { get; init; }
}
public sealed record MediaReconciliationSubjectDto(string Kind, string Id);
public sealed record MediaReconciliationSelectionDto
{
    public required string Filter { get; init; }
    public string? KindId { get; init; }
    public string? SystemKey { get; init; }
    public required long Limit { get; init; }
    public required bool ConversionRequested { get; init; }
    public required long Selected { get; init; }
    public required long Created { get; init; }
    public required long Adopted { get; init; }
    public required long Skipped { get; init; }
    public required long Completed { get; init; }
    public required long Failed { get; init; }
    public required bool EnumerationComplete { get; init; }
}
