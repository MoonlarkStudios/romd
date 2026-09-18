using System.Text.Json;
using System.Data;
using Microsoft.EntityFrameworkCore;
using Romd.Application.Common.ReferenceCatalog;
using Romd.Contracts.Common.ReferenceCatalog;
using Romd.Domain.ReferenceData;
using Romd.Persistence.Entities;

namespace Romd.Persistence.ReferenceData;

internal static partial class TypedReferenceMapping
{
    internal static SystemDefinition Definition(PlatformEntity row) => new(row.BaseName!, row.BaseCompactLabel!, row.BaseDescription, row.BaseAssetHash, row.BaseMonochrome, row.Retired);
    internal static CompanyDefinition Definition(CompanyEntity row) => new(row.BaseName, row.BaseDescription, row.Retired);
    internal static SystemDefinition Effective(PlatformEntity row) => (row.ReferenceOverrides ?? new()).Apply(Definition(row));
    internal static CompanyDefinition Effective(CompanyEntity row) => row.Overrides.Apply(Definition(row));
    internal static SystemOverridesDto Overrides(SystemOverrides x) => new(x.Name, x.CompactLabel, x.Description, x.Icon, x.Monochrome, x.HasDescription, x.HasIcon);
    internal static CompanyOverridesDto Overrides(CompanyOverrides x) => new(x.Name, x.Description, x.HasDescription);
    internal static string ETag<T>(T value) => "\"" + ReferenceCatalogService.Hash(JsonSerializer.SerializeToUtf8Bytes(value, ReferenceJson.Options)) + "\"";
}

public sealed class SystemCompanyReader
{
    private readonly RomdDbContext db;

    public SystemCompanyReader(RomdDbContext db)
    {
        this.db = db;
    }

    public async Task<IReadOnlyDictionary<int, IReadOnlyList<CompanySummaryDto>>> ReadAsync(IEnumerable<int> ids, CancellationToken ct)
    {
        var keys = ids.Distinct().ToArray();
        var links = await db.SystemCompanies.Where(x => keys.Contains(x.PlatformId)).Include(x => x.Company).OrderBy(x => x.CompanyKey).ToListAsync(ct);
        return links.GroupBy(x => x.PlatformId).ToDictionary(g => g.Key,
            g => (IReadOnlyList<CompanySummaryDto>)g.Select(x => new CompanySummaryDto(x.CompanyKey, TypedReferenceMapping.Effective(x.Company).Name)).ToArray());
    }
    public static string? Names(IReadOnlyList<CompanySummaryDto>? values) => values is { Count: > 0 } ? string.Join(", ", values.Select(x => x.Name)) : null;
}
public sealed class SystemReferenceReader : ISystemReferenceReader
{
    private readonly RomdDbContext db;

    public SystemReferenceReader(RomdDbContext db)
    {
        this.db = db;
    }

    public async Task<IReadOnlyList<SystemResourceDto>> ListAsync(CancellationToken ct)
    {
        // A resource spans platform, relationship/company, and artwork queries. Standalone
        // reads need one MVCC snapshot; mutation callers already own the catalog transaction.
        await using var snapshot = db.Database.CurrentTransaction is null
            ? await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct) : null;
        var result = (await ReadAsync(await db.Platforms.Where(x => x.Ownership != null)
            .OrderBy(x => x.CanonicalKey).ToListAsync(ct), ct)).Select(x => x.Resource).ToArray();
        if (snapshot is not null) await snapshot.CommitAsync(ct);
        return result;
    }
    public async Task<ReferenceResourceResult<SystemResourceDto, SystemOverridesDto>?> GetAsync(string key, CancellationToken ct)
    {
        await using var snapshot = db.Database.CurrentTransaction is null
            ? await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct) : null;
        var row = await db.Platforms.SingleOrDefaultAsync(x => x.CanonicalKey == key && x.Ownership != null, ct);
        var result = row is null ? null : (await ReadAsync([row], ct))[0];
        if (snapshot is not null) await snapshot.CommitAsync(ct);
        return result;
    }
    private async Task<IReadOnlyList<ReferenceResourceResult<SystemResourceDto, SystemOverridesDto>>> ReadAsync(IReadOnlyList<PlatformEntity> rows, CancellationToken ct)
    {
        var companies = await new SystemCompanyReader(db).ReadAsync(rows.Select(x => x.Id), ct);
        var hashes = rows.Select(x => TypedReferenceMapping.Effective(x).AssetHash).Where(x => x != null).Distinct().ToArray();
        var types = await db.ReferenceAssets.Where(x => hashes.Contains(x.Hash)).ToDictionaryAsync(x => x.Hash, x => x.ContentType, ct);
        return rows.Select(row =>
        {
            var facts = TypedReferenceMapping.Effective(row);
            var icon = facts.AssetHash is { } hash && types.TryGetValue(hash, out var type) ? new ReferenceAssetDto("/api/assets/" + hash, hash, type, facts.Monochrome) : null;
            var resource = new SystemResourceDto(row.CanonicalKey!, row.Ownership!.Value.ToString(), row.BuiltInVersion,
                facts.Name, facts.CompactLabel, facts.Description, icon, facts.Retired, companies.GetValueOrDefault(row.Id) ?? []);
            var overrides = TypedReferenceMapping.Overrides(row.ReferenceOverrides ?? new());
            return new ReferenceResourceResult<SystemResourceDto, SystemOverridesDto>(resource, overrides,
                TypedReferenceMapping.ETag(new { Resource = resource, Definition = TypedReferenceMapping.Definition(row), Overrides = overrides }));
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

    public async Task<IReadOnlyList<CompanyResourceDto>> ListAsync(CancellationToken ct) =>
        (await db.Companies.OrderBy(x => x.Key).ToListAsync(ct)).Select(x => Describe(x).Resource).ToArray();
    public async Task<ReferenceResourceResult<CompanyResourceDto, CompanyOverridesDto>?> GetAsync(string key, CancellationToken ct) =>
        await db.Companies.SingleOrDefaultAsync(x => x.Key == key, ct) is { } row ? Describe(row) : null;
    private static ReferenceResourceResult<CompanyResourceDto, CompanyOverridesDto> Describe(CompanyEntity row)
    {
        var facts = TypedReferenceMapping.Effective(row);
        var resource = new CompanyResourceDto(row.Key, row.Ownership.ToString(), row.BuiltInVersion, facts.Name, facts.Description, facts.Retired);
        var overrides = TypedReferenceMapping.Overrides(row.Overrides);
        return new(resource, overrides, TypedReferenceMapping.ETag(new { Resource = resource, Definition = TypedReferenceMapping.Definition(row), Overrides = overrides }));
    }
}
