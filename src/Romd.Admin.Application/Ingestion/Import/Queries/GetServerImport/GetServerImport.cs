using ErrorOr;
using Romd.Application.Common.Configuration;
using Romd.Application.Common.Cqrs;

namespace Romd.Admin.Application.Ingestion.Import.Queries.GetServerImport;

public sealed record GetServerImportQuery : IQuery<ServerImportView>;

public sealed class GetServerImportQueryHandler(IServerImportOptions options, IServerImportStore store, TimeProvider clock)
    : IQueryHandler<GetServerImportQuery, ServerImportView>
{
    public async Task<ErrorOr<ServerImportView>> HandleAsync(GetServerImportQuery query, CancellationToken ct = default)
    {
        if (options.ServerImportDirectory is not { } directory)
            return ServerImportView.Create(options, clock);

        var activity = await store.ReadActivityAsync(directory, ct);
        var job = await store.ReadJobAsync(activity?.JobId, ct);
        return ServerImportView.Create(options, clock, activity, job);
    }
}
