using ErrorOr;
using Romd.Application.Common.Cqrs;
using Romd.Application.Common.Security;
using Romd.Contracts.Consumer.Account;

namespace Romd.Consumer.Application.Account.Queries.GetConsumerUserSettings;

public sealed record GetConsumerUserSettingsQuery : IQuery<ConsumerUserSettings>;

public sealed class GetConsumerUserSettingsQueryHandler(
    ICurrentUser currentUser,
    IConsumerUserSettingsStore settingsStore,
    IConsumerTaxonomyKeys taxonomyKeys)
    : IQueryHandler<GetConsumerUserSettingsQuery, ConsumerUserSettings>
{
    public async Task<ErrorOr<ConsumerUserSettings>> HandleAsync(
        GetConsumerUserSettingsQuery query,
        CancellationToken ct = default)
    {
        if (!currentUser.IsAuthenticated || currentUser.UserId is not { } userId)
        {
            return ConsumerErrors.CurrentUserRequired();
        }

        var settings = await settingsStore.GetSettingsAsync(userId, ct);

        return await settings.ToDtoAsync(taxonomyKeys, ct);
    }
}
