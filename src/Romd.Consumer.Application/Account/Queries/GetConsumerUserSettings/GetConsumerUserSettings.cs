using ErrorOr;
using Romd.Application.Common.Cqrs;
using Romd.Application.Common.Security;
using Romd.Contracts.Consumer.Account;

namespace Romd.Consumer.Application.Account.Queries.GetConsumerUserSettings;

public sealed record GetConsumerUserSettingsQuery : IQuery<ConsumerUserSettingsDto>;

public sealed class GetConsumerUserSettingsQueryHandler(
    ICurrentUser currentUser,
    IConsumerUserSettingsStore settingsStore)
    : IQueryHandler<GetConsumerUserSettingsQuery, ConsumerUserSettingsDto>
{
    public async Task<ErrorOr<ConsumerUserSettingsDto>> HandleAsync(
        GetConsumerUserSettingsQuery query,
        CancellationToken ct = default)
    {
        if (!currentUser.IsAuthenticated || currentUser.UserId is not { } userId)
        {
            return ConsumerErrors.CurrentUserRequired();
        }

        var settings = await settingsStore.GetSettingsAsync(userId, ct);

        return settings.ToDto();
    }
}
