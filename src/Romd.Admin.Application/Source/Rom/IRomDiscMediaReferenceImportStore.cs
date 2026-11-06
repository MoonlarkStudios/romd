using ErrorOr;
using Romd.Domain.Source.Rom;

namespace Romd.Admin.Application.Source.Rom;

/// <summary>Stages an immutable, caller-admitted reference and its exact raw body.</summary>
/// <remarks>Requires the caller's active ReadCommitted unit-of-work transaction and refuses other isolation levels.
/// The assertion guard precedes insertion and fanout, so scans observe earlier committed observations.
/// Never fetches, flushes or commits. Copies and validates raw custody before retaining it.
/// An existing Guid is a replay only when every record field and raw byte matches; otherwise returns
/// ConflictingIdentity. Distinct Guids are retained even when the published quartet matches.
/// Shape/source labels cannot authenticate retrieval; only trusted import composition may call this port.</remarks>
public interface IRomDiscMediaReferenceImportStore
{
    /// <summary>Reads the actual database role before import admission, without a transaction or writes.</summary>
    Task<string> GetRuntimeRoleAsync(CancellationToken ct = default);

    Task<ErrorOr<RomDiscMediaReferenceRecord>> StageAsync(RomDiscMediaReferenceRecord record,
        ReadOnlyMemory<byte> rawBody, CancellationToken ct = default);
}

public static class RomDiscMediaReferenceImportErrors
{
    public static Error InvalidIdentity() => Error.Validation("Rom.DiscMediaReferenceImportIdentity",
        "A supplied retrieval identity must be a nonempty Guid.");
    public static Error ConflictingIdentity() => Error.Conflict("Rom.DiscMediaReferenceImportConflict",
        "The reference identity is already bound to different immutable facts or raw bytes.");
    public static Error Unavailable() => Error.Failure("Rom.DiscMediaReferenceImportUnavailable",
        "The local reference import store is unavailable.");
    public static Error InvalidStagedResult() => Error.Validation("Rom.DiscMediaReferenceImportResult",
        "The import store did not retain the exact prepared reference.");
}
