using ErrorOr;
using Romd.Application.Common.Cqrs;
using Romd.Contracts.Management.Models;

namespace Romd.Admin.Application.Ingestion.Import.Queries.ListImportFolders;

/// <summary>Lists the allowed import roots (no path) or the folders directly inside one of them.</summary>
public sealed record ListImportFoldersQuery(string? Path = null) : IQuery<ImportFolderListing>;

/// <summary>
///     Read-only view of the folders under the configured allowed import roots. Every listed path
///     passes the same validation as an import, so browsing never reaches beyond what importing could.
/// </summary>
public interface IImportFolderBrowser
{
    ErrorOr<ImportFolderListing> List(string? path, CancellationToken ct = default);
}

public sealed class ListImportFoldersQueryHandler(IImportFolderBrowser browser)
    : IQueryHandler<ListImportFoldersQuery, ImportFolderListing>
{
    public Task<ErrorOr<ImportFolderListing>> HandleAsync(ListImportFoldersQuery query, CancellationToken ct = default) =>
        Task.FromResult(browser.List(string.IsNullOrWhiteSpace(query.Path) ? null : query.Path, ct));
}
