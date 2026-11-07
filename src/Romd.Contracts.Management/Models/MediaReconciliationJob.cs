namespace Romd.Contracts.Management.Models;

/// <summary>One media subject or a finite selection of subjects. Outcomes come from the committed reconciliation journal.</summary>
public sealed record MediaReconciliationJob : Job
{
    public required string RequestMode { get; init; }
    public MediaReconciliationSubject? Subject { get; init; }
    public MediaReconciliationSelection? Selection { get; init; }
    public Guid? OriginSelectionJobId { get; init; }
    public string? Outcome { get; init; }
    public string? OutcomeCode { get; init; }
}
public sealed record MediaReconciliationSubject(string Kind, string Id);
public sealed record MediaReconciliationSelection
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
