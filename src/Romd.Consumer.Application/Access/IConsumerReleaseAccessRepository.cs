using Romd.Consumer.Application.Libraries;

namespace Romd.Consumer.Application.Access;

public sealed record ConsumerReleaseAccessRequest(
    ConsumerLibraryScope Scope,
    int ReleaseId);

public sealed record ConsumerReleaseAccessDecision(
    bool Allowed,
    int? TitleId)
{
    public static ConsumerReleaseAccessDecision Allow(int titleId) => new(true, titleId);

    public static ConsumerReleaseAccessDecision Revoke() => new(false, null);
}

public interface IConsumerReleaseAccessRepository
{
    Task<ConsumerLibraryReadResult<ConsumerReleaseAccessDecision>> GetAccessAsync(
        ConsumerReleaseAccessRequest request,
        CancellationToken ct = default);
}
