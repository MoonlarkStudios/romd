using System.Text.Json;
using System.Data;
using Microsoft.EntityFrameworkCore;
using Romd.Application.Common.ReferenceCatalog;
using Romd.Contracts.Common.ReferenceCatalog;
using Romd.Contracts.Common.Models;
using Romd.Domain.ReferenceData;
using Romd.Persistence.Entities;

using ContractCompanyOverrides = Romd.Contracts.Common.ReferenceCatalog.CompanyOverrides;
using ContractSystemOverrides = Romd.Contracts.Common.ReferenceCatalog.SystemOverrides;

using CompanyOverrides = Romd.Domain.ReferenceData.CompanyOverrides;
using SystemOverrides = Romd.Domain.ReferenceData.SystemOverrides;

namespace Romd.Persistence.ReferenceData;

internal static partial class TypedReferenceMapping
{
    internal static SystemDefinition Definition(PlatformEntity row) => new(row.BaseName!, row.BaseCompactLabel!, row.BaseDescription, row.BaseAssetHash, row.BaseMonochrome, row.Retired);
    internal static CompanyDefinition Definition(CompanyEntity row) => new(row.BaseName, row.BaseDescription, row.Retired);
    internal static SystemDefinition Effective(PlatformEntity row) => (row.ReferenceOverrides ?? new()).Apply(Definition(row));
    internal static CompanyDefinition Effective(CompanyEntity row) => row.Overrides.Apply(Definition(row));
    internal static ContractSystemOverrides Overrides(SystemOverrides x) => new(x.Name, x.CompactLabel, x.Description, x.Icon, x.Monochrome, x.HasDescription, x.HasIcon);
    internal static ContractCompanyOverrides Overrides(CompanyOverrides x) => new(x.Name, x.Description, x.HasDescription);
    internal static string Revision<T>(T value) => ReferenceCatalogService.Hash(JsonSerializer.SerializeToUtf8Bytes(value, ReferenceJson.Options));
}

public sealed class SystemCompanyReader
{
    private readonly RomdDbContext db;

    public SystemCompanyReader(RomdDbContext db)
    {
        this.db = db;
    }

    public async Task<IReadOnlyDictionary<int, IReadOnlyList<CompanySummary>>> ReadAsync(IEnumerable<int> ids, CancellationToken ct)
    {
        var keys = ids.Distinct().ToArray();
        var links = await db.SystemCompanies.Where(x => keys.Contains(x.PlatformId)).Include(x => x.Company).OrderBy(x => x.CompanyKey).ToListAsync(ct);
        return links.GroupBy(x => x.PlatformId).ToDictionary(g => g.Key,
            g => (IReadOnlyList<CompanySummary>)g.Select(x => new CompanySummary(x.CompanyKey, TypedReferenceMapping.Effective(x.Company).Name)).ToArray());
    }
    public static string? Names(IReadOnlyList<CompanySummary>? values) => values is { Count: > 0 } ? string.Join(", ", values.Select(x => x.Name)) : null;
}
public sealed class SystemReferenceReader : ISystemReferenceReader
{
    private readonly RomdDbContext db;

    public SystemReferenceReader(RomdDbContext db)
    {
        this.db = db;
    }

    public async Task<Page<SystemResource>> ListAsync(ReferencePageRequest request, CancellationToken ct)
    {
        await using var snapshot = db.Database.CurrentTransaction is null
            ? await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct) : null;
        var query = db.Platforms.Where(x => x.Ownership != null);
        if (request.Query.Length > 0)
            query = query.Where(x => ReferenceSearch.Normalize(x.Name).Contains(request.Query)
                || ReferenceSearch.Normalize(x.ShortName).Contains(request.Query)
                || ReferenceSearch.Normalize(x.CanonicalKey!).Contains(request.Query)
                || ReferenceSearch.Normalize(x.Manufacturer ?? "").Contains(request.Query));
        if (request.AfterKey is { } after)
            query = query.Where(x => string.Compare(EF.Functions.Collate(x.CanonicalKey!, "C"), after) > 0);
        var rows = await query.OrderBy(x => EF.Functions.Collate(x.CanonicalKey!, "C")).Take(request.Limit + 1).ToListAsync(ct);
        var more = rows.Count > request.Limit;
        var selected = rows.Take(request.Limit).ToArray();
        var items = (await ReadAsync(selected, ct)).Select(x => x.Resource).ToArray();
        if (snapshot is not null)
            await snapshot.CommitAsync(ct);
        return new Page<SystemResource>
        {
            Items = items,
            HasNextPage = more,
            NextCursor = more ? request.CursorFor(items[^1].Key) : null
        };
    }
    public async Task<ReferenceResourceResult<SystemResource, ContractSystemOverrides>?> GetAsync(string key, CancellationToken ct)
    {
        await using var snapshot = db.Database.CurrentTransaction is null
            ? await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct) : null;
        var row = await db.Platforms.SingleOrDefaultAsync(x => x.CanonicalKey == key && x.Ownership != null, ct);
        var result = row is null ? null : (await ReadAsync([row], ct))[0];
        if (snapshot is not null)
            await snapshot.CommitAsync(ct);
        return result;
    }
    private async Task<IReadOnlyList<ReferenceResourceResult<SystemResource, ContractSystemOverrides>>> ReadAsync(IReadOnlyList<PlatformEntity> rows, CancellationToken ct)
    {
        var companies = await new SystemCompanyReader(db).ReadAsync(rows.Select(x => x.Id), ct);
        var hashes = rows.Select(x => TypedReferenceMapping.Effective(x).AssetHash).Where(x => x != null).Distinct().ToArray();
        var types = await db.ReferenceAssets.Where(x => hashes.Contains(x.Hash)).ToDictionaryAsync(x => x.Hash, x => x.ContentType, ct);
        return rows.Select(row =>
        {
            var facts = TypedReferenceMapping.Effective(row);
            var icon = facts.AssetHash is { } hash && types.TryGetValue(hash, out var type) ? new ReferenceAsset("/api/assets/" + hash, hash, type, facts.Monochrome) : null;
            var resource = new SystemResource(row.CanonicalKey!, row.Ownership!.Value.ToString(), row.BuiltInVersion,
                facts.Name, facts.CompactLabel, facts.Description, icon, facts.Retired, companies.GetValueOrDefault(row.Id) ?? [])
            { Revision = "" };
            var overrides = TypedReferenceMapping.Overrides(row.ReferenceOverrides ?? new());
            var revision = TypedReferenceMapping.Revision(new { Resource = resource, Definition = TypedReferenceMapping.Definition(row), Overrides = overrides });
            return new ReferenceResourceResult<SystemResource, ContractSystemOverrides>(resource with { Revision = revision }, overrides, revision);
        }).ToArray();
    }
}
public sealed class CompanyReferenceReader : ICompanyReferenceReader
{
    private readonly RomdDbContext db;

    public CompanyReferenceReader(RomdDbContext db)
    {
        this.db = db;
    }

    public async Task<Page<CompanyResource>> ListAsync(ReferencePageRequest request, CancellationToken ct)
    {
        var query = db.Companies.AsQueryable();
        if (request.Query.Length > 0)
            query = query.Where(x => ReferenceSearch.Normalize(x.Name).Contains(request.Query)
            || ReferenceSearch.Normalize(x.Key).Contains(request.Query));
        if (request.AfterKey is { } after)
            query = query.Where(x => string.Compare(EF.Functions.Collate(x.Key, "C"), after) > 0);
        var rows = await query.OrderBy(x => EF.Functions.Collate(x.Key, "C")).Take(request.Limit + 1).ToListAsync(ct);
        var more = rows.Count > request.Limit;
        var items = rows.Take(request.Limit).Select(x => Describe(x).Resource).ToArray();
        return new Page<CompanyResource>
        {
            Items = items,
            HasNextPage = more,
            NextCursor = more ? request.CursorFor(items[^1].Key) : null
        };
    }
    public async Task<ReferenceResourceResult<CompanyResource, ContractCompanyOverrides>?> GetAsync(string key, CancellationToken ct) =>
        await db.Companies.SingleOrDefaultAsync(x => x.Key == key, ct) is { } row ? Describe(row) : null;
    private static ReferenceResourceResult<CompanyResource, ContractCompanyOverrides> Describe(CompanyEntity row)
    {
        var facts = TypedReferenceMapping.Effective(row);
        var resource = new CompanyResource(row.Key, row.Ownership.ToString(), row.BuiltInVersion, facts.Name, facts.Description, facts.Retired) { Revision = "" };
        var overrides = TypedReferenceMapping.Overrides(row.Overrides);
        var revision = TypedReferenceMapping.Revision(new { Resource = resource, Definition = TypedReferenceMapping.Definition(row), Overrides = overrides });
        return new(resource with { Revision = revision }, overrides, revision);
    }
}
