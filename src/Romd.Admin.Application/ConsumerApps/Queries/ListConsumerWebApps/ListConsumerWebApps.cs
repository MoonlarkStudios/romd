using ErrorOr;
using Romd.Application.Common.Cqrs;
using Romd.Application.Common.Pagination;
using Romd.Application.Common.Security;
using Romd.Contracts.Common.Models;
using Romd.Contracts.Management.ConsumerApps;

namespace Romd.Admin.Application.ConsumerApps.Queries.ListConsumerWebApps;

public sealed record ListConsumerWebAppsQuery(string? Cursor, int Limit) : IQuery<Page<ConsumerWebApp>>
{
    public const int MaxLimit = 100;
}

public sealed class ListConsumerWebAppsQueryHandler(IConsumerWebAppAdministration apps)
    : IQueryHandler<ListConsumerWebAppsQuery, Page<ConsumerWebApp>>
{
    private static readonly string _fingerprint = KeysetCursor.Fingerprint("admin.consumer-web-apps", "displayName,clientId:asc");

    public async Task<ErrorOr<Page<ConsumerWebApp>>> HandleAsync(ListConsumerWebAppsQuery query, CancellationToken ct = default)
    {
        if (query.Limit is < 1 or > ListConsumerWebAppsQuery.MaxLimit)
            return Error.Validation("Pagination.InvalidLimit", $"The page size must be between 1 and {ListConsumerWebAppsQuery.MaxLimit}.");

        var after = KeysetCursor.Decode<ConsumerWebAppPosition>(query.Cursor, _fingerprint,
            position => position.DisplayName is { Length: > 0 and <= 120 } &&
                position.ClientId is { Length: <= 100 } && ConsumerWebAppRegistration.IsClientId(position.ClientId), maxLength: 2048);
        if (after.IsError)
            return after.Errors;

        var rows = await apps.ListAsync(after.Value, query.Limit + 1, ct);
        var items = rows.Take(query.Limit).Select(ConsumerWebAppMapping.ToContract).ToArray();
        bool hasNextPage = rows.Count > query.Limit;
        return new Page<ConsumerWebApp>
        {
            Items = items,
            HasNextPage = hasNextPage,
            NextCursor = hasNextPage ? KeysetCursor.Encode(new ConsumerWebAppPosition(items[^1].DisplayName, items[^1].ClientId), _fingerprint) : null
        };
    }
}
