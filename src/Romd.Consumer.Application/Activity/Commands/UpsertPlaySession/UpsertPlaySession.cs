using ErrorOr;
using Romd.Application.Common.Cqrs;
using Romd.Application.Common.Ids;
using Romd.Application.Common.Security;
using Romd.Consumer.Application.Access;
using Romd.Consumer.Application.Libraries;
using Romd.Contracts.Consumer.Activity;
using Romd.Domain.Activity;

namespace Romd.Consumer.Application.Activity.Commands.UpsertPlaySession;

public sealed record UpsertPlaySessionCommand(Guid SessionId, UpsertPlaySessionRequest Request)
    : ICommand<PlaySessionDto>;

public sealed class UpsertPlaySessionCommandHandler(
    ICurrentUser currentUser,
    IConsumerReleaseAccessRepository accessRepository,
    IPlayActivityRepository activityRepository,
    IPlayActivityUnitOfWork unitOfWork)
    : ICommandHandler<UpsertPlaySessionCommand, PlaySessionDto>
{
    public async Task<ErrorOr<PlaySessionDto>> HandleAsync(
        UpsertPlaySessionCommand command,
        CancellationToken ct = default)
    {
        if (!currentUser.IsAuthenticated || currentUser.UserId is not { } userId)
        {
            return ConsumerErrors.CurrentUserRequired();
        }

        var request = command.Request;
        if (command.SessionId == Guid.Empty ||
            string.IsNullOrWhiteSpace(request.ClientId) ||
            request.ClientId.Length > 200 ||
            request.ClientId != request.ClientId.Trim())
        {
            return ConsumerErrors.InvalidPlaySession("A non-empty session ID and a 1-200 character client ID are required.");
        }

        if (!IdCoder.TryDecode(request.TitleId, out int titleId) ||
            !IdCoder.TryDecode(request.ReleaseId, out int releaseId))
        {
            return ConsumerErrors.InvalidPlaySession("titleId and releaseId must be valid ROMD IDs.");
        }

        var startedAt = request.StartedAt.ToUniversalTime();
        var endedAt = request.EndedAt?.ToUniversalTime();
        if (endedAt < startedAt)
        {
            return ConsumerErrors.InvalidPlaySession("endedAt must be greater than or equal to startedAt.");
        }

        if (request.ActiveDurationSeconds is < 0 or > PlaySession.MaximumActiveDurationSeconds)
        {
            return ConsumerErrors.InvalidPlaySession(
                $"activeDurationSeconds must be between 0 and {PlaySession.MaximumActiveDurationSeconds}.");
        }

        var access = await accessRepository.GetAccessAsync(
            new ConsumerReleaseAccessRequest(new ConsumerLibraryScope(userId), releaseId), ct);
        if (access is not ConsumerLibraryReadResult<ConsumerReleaseAccessDecision>.Found
            { Value: { Allowed: true, TitleId: { } accessibleTitleId } } ||
            accessibleTitleId != titleId)
        {
            return access is ConsumerLibraryReadResult<ConsumerReleaseAccessDecision>.LibraryUnavailable or
                ConsumerLibraryReadResult<ConsumerReleaseAccessDecision>.ProjectionInconsistent
                ? ConsumerErrors.CurrentLibraryUnavailable()
                : ConsumerErrors.TitleNotFound();
        }

        await using var transaction = await unitOfWork.BeginAsync(userId, ct);
        var result = await activityRepository.UpsertAsync(
            userId,
            command.SessionId,
            new PlaySessionSnapshot(
                request.ClientId,
                titleId,
                releaseId,
                startedAt,
                endedAt,
                request.ActiveDurationSeconds),
            ct);

        if (result.Status is PlaySessionUpsertStatus.Created or PlaySessionUpsertStatus.Updated or
            PlaySessionUpsertStatus.Unchanged)
        {
            await transaction.CommitAsync(ct);
        }

        return result.Status switch
        {
            PlaySessionUpsertStatus.Created or PlaySessionUpsertStatus.Updated or PlaySessionUpsertStatus.Unchanged
                when result.Session is not null => result.Session.ToContract(),
            PlaySessionUpsertStatus.Conflict => ConsumerErrors.PlaySessionConflict(),
            _ => throw new InvalidOperationException("Unknown play-session upsert result.")
        };
    }
}
