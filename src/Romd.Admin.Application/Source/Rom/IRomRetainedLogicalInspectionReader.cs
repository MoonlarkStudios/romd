using ErrorOr;
using Romd.Domain.Source.Rom;

namespace Romd.Admin.Application.Source.Rom;

/// <summary>Reads an exact committed raw logical inspection; callers reread under logical ownership locks before publishing authority.</summary>
public interface IRomRetainedLogicalInspectionReader
{
    Task<ErrorOr<RomRetainedLogicalInspection?>> ReadAsync(Guid inspectionId, CancellationToken ct = default);
}

public sealed record RomRetainedLogicalInspection(int RomId, RomLogicalInspection Observation);

public static class RomRetainedLogicalInspectionErrors
{
    public static Error Unavailable => Error.Failure("Rom.LogicalInspectionUnavailable", "The committed logical inspection reader is unavailable.");
    public static Error Conflicting => Error.Conflict("Rom.LogicalInspectionConflict", "Retained logical observations contradict their immutable identity.");
    public static Error StagedChanges => Error.Conflict("Rom.LogicalInspectionStaged", "Uncommitted observations or ownership changes cannot authorize replay.");
    public static Error InvalidIdentity => Error.Validation("Rom.LogicalInspectionIdentity", "An exact retained inspection identity is required.");
}
