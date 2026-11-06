using Romd.Domain.Catalog;

namespace Romd.Admin.Application.Search;

/// <summary>
///     Filter parameters for game search queries.
/// </summary>
/// <param name="PlatformId">Filter by platform ID (resolved from Sqid).</param>
/// <param name="Year">Filter by release year.</param>
/// <param name="Manufacturer">Filter by manufacturer name.</param>
/// <param name="RegionId">Filter by region ID (resolved from junction table).</param>
/// <param name="BiosFilter">How to handle BIOS entries.</param>
public sealed record GameSearchFilters(
    int? PlatformId = null,
    string? Year = null,
    string? Manufacturer = null,
    int? RegionId = null,
    BiosFilter BiosFilter = BiosFilter.Exclude);
