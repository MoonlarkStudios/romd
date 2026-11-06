using Romd.Admin.Application.Taxonomy;
using Romd.Admin.Application.Taxonomy.ReadModels;
using Romd.Application.Common.Ids;
using Romd.Application.Common.Pagination;
using Romd.Contracts.Common.Models;
using Romd.Contracts.Management.Taxonomy;

namespace Romd.Host.Endpoints;

/// <summary>The responses the admin region and language routes share.</summary>
internal static class TaxonomyResponses
{
    internal static AliasDto ToDto(TaxonomyAlias alias) => new()
    {
        Id = IdCoder.Encode(alias.Id),
        Alias = alias.Alias,
        Ownership = alias.Ownership
    };

    internal static Page<AliasDto> ToDto(PagedList<TaxonomyAlias> page) => new()
    {
        Items = page.Items.Select(ToDto).ToList(),
        NextCursor = page.NextCursor,
        HasNextPage = page.HasNextPage
    };

    /// <summary>The first alias page a row embeds; its cursor continues through the row's alias collection.</summary>
    internal static Page<AliasDto> ToDto(string collection, string key, TaxonomyAliasPage page) => new()
    {
        Items = page.Items.Select(ToDto).ToList(),
        NextCursor = page.HasNextPage ? TaxonomyAliasPageRequest.CursorFor(collection, key, page.Items[^1].Id) : null,
        HasNextPage = page.HasNextPage
    };

    internal static string AliasLocation(string collection, string key, TaxonomyAlias alias) =>
        $"/api/{collection}/{Uri.EscapeDataString(key)}/aliases/{IdCoder.Encode(alias.Id)}";
}
