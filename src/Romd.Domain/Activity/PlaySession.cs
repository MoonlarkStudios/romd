namespace Romd.Domain.Activity;

public enum PlaySessionMergeResult
{
    Unchanged,
    Updated,
    Conflict
}

public sealed class PlaySession
{
    public const int MaximumActiveDurationSeconds = 366 * 24 * 60 * 60;

    public Guid UserId { get; }
    public Guid SessionId { get; }
    public string ClientId { get; }
    public int TitleId { get; }
    public int ReleaseId { get; }
    public DateTimeOffset StartedAt { get; }
    public DateTimeOffset? EndedAt { get; private set; }
    public int? ActiveDurationSeconds { get; private set; }
    public DateTimeOffset CreatedAt { get; }
    public DateTimeOffset UpdatedAt { get; private set; }

    public PlaySession(
        Guid userId,
        Guid sessionId,
        string clientId,
        int titleId,
        int releaseId,
        DateTimeOffset startedAt,
        DateTimeOffset? endedAt,
        int? activeDurationSeconds,
        DateTimeOffset createdAt,
        DateTimeOffset updatedAt)
    {
        UserId = userId;
        SessionId = sessionId;
        ClientId = clientId;
        TitleId = titleId;
        ReleaseId = releaseId;
        StartedAt = startedAt;
        EndedAt = endedAt;
        ActiveDurationSeconds = activeDurationSeconds;
        CreatedAt = createdAt;
        UpdatedAt = updatedAt;
    }

    public PlaySessionMergeResult Merge(PlaySessionSnapshot snapshot, DateTimeOffset updatedAt)
    {
        if (snapshot.ClientId != ClientId ||
            snapshot.TitleId != TitleId ||
            snapshot.ReleaseId != ReleaseId ||
            snapshot.StartedAt != StartedAt ||
            (EndedAt is not null && snapshot.EndedAt is not null && EndedAt != snapshot.EndedAt) ||
            (ActiveDurationSeconds is not null && snapshot.ActiveDurationSeconds is not null &&
             ActiveDurationSeconds != snapshot.ActiveDurationSeconds))
        {
            return PlaySessionMergeResult.Conflict;
        }

        bool changed = false;
        if (EndedAt is null && snapshot.EndedAt is not null)
        {
            EndedAt = snapshot.EndedAt;
            changed = true;
        }

        if (ActiveDurationSeconds is null && snapshot.ActiveDurationSeconds is not null)
        {
            ActiveDurationSeconds = snapshot.ActiveDurationSeconds;
            changed = true;
        }

        if (changed)
        {
            UpdatedAt = updatedAt;
            return PlaySessionMergeResult.Updated;
        }

        return PlaySessionMergeResult.Unchanged;
    }
}

public sealed record PlaySessionSnapshot(
    string ClientId,
    int TitleId,
    int ReleaseId,
    DateTimeOffset StartedAt,
    DateTimeOffset? EndedAt,
    int? ActiveDurationSeconds);
