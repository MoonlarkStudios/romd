using ErrorOr;
using Romd.Application.Common.Cqrs;
using Romd.Contracts.Management.ConsumerApps;

namespace Romd.Admin.Application.ConsumerApps.Queries.ListConsumerWebApps;

public sealed record ListConsumerWebAppsQuery : IQuery<IReadOnlyList<ConsumerWebApp>>;

public sealed class ListConsumerWebAppsQueryHandler(IConsumerWebAppAdministration apps)
    : IQueryHandler<ListConsumerWebAppsQuery, IReadOnlyList<ConsumerWebApp>>
{
    public async Task<ErrorOr<IReadOnlyList<ConsumerWebApp>>> HandleAsync(ListConsumerWebAppsQuery query, CancellationToken ct = default) =>
        (await apps.ListAsync(ct)).Select(ConsumerWebAppMapping.ToContract).ToArray();
}
