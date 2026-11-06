using System.Collections.Frozen;
using ErrorOr;
using Romd.Contracts.Management.Artwork;
using Romd.Domain.Jobs;

namespace Romd.Admin.Application.Artwork;

/// <summary>Database-only acceptance capabilities. Every lock/read after retry-mode lookup requires the owner's ReadCommitted transaction.</summary>
public interface IArtworkCompletionAcceptance
{
    Task<string?> ReadRetryModeAsync(Guid jobId, CancellationToken ct);
    Task LockRequestAsync(Guid requestId, CancellationToken ct);
    Task<ArtworkCompletionRequestIdentity?> FindRequestAsync(Guid requestId, CancellationToken ct);
    Task LockOwnersAsync(CancellationToken ct);
    Task<Guid?> FindActiveSelectedAsync(string signature, CancellationToken ct);
    Task<ErrorOr<ArtworkCompletionScopeSnapshot>> SnapshotScopeAsync(ArtworkCompletionRequest request, CancellationToken ct);
    /// <summary>Unfinished owners without follow-ups, in Pending/newest-created/ID preference order; modes are read once per owner.</summary>
    Task<IReadOnlyList<ArtworkCompletionOwner>> ReadOwnersAsync(IReadOnlyList<int> titleIds, CancellationToken ct);
    IStagedArtworkCompletionAcceptance AddJobStaged(ArtworkCompletionJob job, string signature,
        IReadOnlyList<ArtworkCompletionPlannedItem> items);
    Task InsertItemsStagedAsync(IStagedArtworkCompletionAcceptance receipt, DateTimeOffset acceptedAt, CancellationToken ct);
    Task AttachRetryStagedAsync(IStagedArtworkCompletionAcceptance receipt, Guid originalJobId, CancellationToken ct);
}

/// <summary>Null CompletionSignature denotes an ID belonging to another job family.</summary>
public sealed record ArtworkCompletionRequestIdentity(Guid JobId, string? CompletionSignature);
public sealed record ArtworkCompletionTitle(int TitleId, string TitleName);
public sealed record ArtworkCompletionOwner(int TitleId, Guid JobId, string Mode, bool RequireTracked);
public sealed record ArtworkCompletionPlannedItem(int TitleId, string TitleName, bool RequireTracked, Guid? FollowUpJobId);

public sealed class ArtworkCompletionScopeSnapshot(IEnumerable<ArtworkCompletionTitle> titles, IEnumerable<int> trackedRetryIds)
{
    public IReadOnlyList<ArtworkCompletionTitle> Titles { get; } = Array.AsReadOnly(titles.ToArray());
    public IReadOnlySet<int> TrackedRetryIds { get; } = trackedRetryIds.ToFrozenSet();
}

/// <summary>The job/dispatch row must be owner-flushed before item FKs; this proof expires with its original transaction/tracking.</summary>
public interface IStagedArtworkCompletionAcceptance
{
    Guid ReadAfterFlush();
}
