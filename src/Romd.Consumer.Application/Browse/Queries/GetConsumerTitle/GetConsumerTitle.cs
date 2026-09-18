using ErrorOr;
using Romd.Application.Common.Cqrs;
using Romd.Application.Common.Security;
using Romd.Consumer.Application.Account;
using Romd.Consumer.Application.Browse.ReadModels;
using Romd.Consumer.Application.Libraries;
using Romd.Contracts.Consumer.Browse;

namespace Romd.Consumer.Application.Browse.Queries.GetConsumerTitle;

public sealed record GetConsumerTitleQuery(int TitleId) : IQuery<ConsumerTitleDetailDto>;

public sealed class GetConsumerTitleQueryHandler(
    ICurrentUser currentUser,
    IConsumerUserSettingsStore settingsStore,
    IConsumerBrowseRepository browseRepository)
    : IQueryHandler<GetConsumerTitleQuery, ConsumerTitleDetailDto>
{
    public async Task<ErrorOr<ConsumerTitleDetailDto>> HandleAsync(
        GetConsumerTitleQuery query,
        CancellationToken ct = default)
    {
        if (!currentUser.IsAuthenticated || currentUser.UserId is not { } userId)
        {
            return ConsumerErrors.CurrentUserRequired();
        }

        var settings = await settingsStore.GetSettingsAsync(userId, ct);
        var result = await browseRepository.GetTitleAsync(
            new ConsumerLibraryScope(userId),
            query.TitleId,
            settings.ReleasePreference,
            ct);

        return result switch
        {
            ConsumerLibraryReadResult<ConsumerTitleDetailData>.Found found => found.Value.ToContract(),
            ConsumerLibraryReadResult<ConsumerTitleDetailData>.ItemNotFound => ConsumerErrors.TitleNotFound(),
            ConsumerLibraryReadResult<ConsumerTitleDetailData>.LibraryUnavailable =>
                ConsumerErrors.LibraryNotFound(),
            ConsumerLibraryReadResult<ConsumerTitleDetailData>.ProjectionInconsistent =>
                ConsumerErrors.TitleNotFound(),
            _ => throw new InvalidOperationException("Unknown Consumer Library title-detail result.")
        };
    }
}
