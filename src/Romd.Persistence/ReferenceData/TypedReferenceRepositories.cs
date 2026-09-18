using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Romd.Admin.Application.ReferenceData;
using Romd.Domain.ReferenceData;
using Romd.Domain.Source.Platform;
using Romd.Persistence.Entities;

namespace Romd.Persistence.ReferenceData;

public sealed class SystemReferenceRepository : ISystemReferenceRepository
{
    private readonly RomdDbContext db;

    public SystemReferenceRepository(RomdDbContext db)
    {
        this.db = db;
    }

    public async Task<SystemReference?> GetAsync(string key, CancellationToken ct)
    {
        var row = await db.Platforms.SingleOrDefaultAsync(x => x.CanonicalKey == key && x.Ownership != null, ct);
        return row is null ? null : new(row.Id, key, new(row.Ownership!.Value, row.BuiltInVersion), TypedReferenceMapping.Definition(row), row.ReferenceOverrides ?? new(),
            await db.SystemCompanies.Where(x => x.PlatformId == row.Id).OrderBy(x => x.CompanyKey).Select(x => x.CompanyKey).ToArrayAsync(ct));
    }
    public Task<bool> KeyExistsAsync(string key, CancellationToken ct) => db.Platforms.AnyAsync(x => x.CanonicalKey == key || x.ShortName == key, ct);
    public Task<bool> AssetExistsAsync(string hash, CancellationToken ct) => db.ReferenceAssets.AnyAsync(x => x.Hash == hash, ct);
    public async Task StageAsync(SystemReference system, CancellationToken ct)
    {
        var row = system.Id == 0 ? null : await db.Platforms.AsTracking().SingleAsync(x => x.Id == system.Id, ct);
        if (row is null)
        { row = PlatformEntity.FromDomain(Platform.CreateNew(system.Effective.Name, system.Key)); row.CanonicalKey = system.Key; db.Platforms.Add(row); }
        row.Ownership = system.Metadata.Ownership;
        row.BuiltInVersion = system.Metadata.BuiltInVersion;
        row.BaseName = system.Definition.Name;
        row.BaseCompactLabel = system.Definition.CompactLabel;
        row.BaseDescription = system.Definition.Description;
        row.BaseAssetHash = system.Definition.AssetHash;
        row.BaseMonochrome = system.Definition.Monochrome;
        row.Retired = system.Definition.Retired;
        row.ReferenceOverrides = system.Overrides;
        var links = await db.SystemCompanies.AsTracking().Where(x => x.PlatformId == row.Id).ToListAsync(ct);
        db.SystemCompanies.RemoveRange(links.Where(x => !system.ManufacturerKeys.Contains(x.CompanyKey, StringComparer.Ordinal)));
        foreach (var key in system.ManufacturerKeys.Except(links.Select(x => x.CompanyKey), StringComparer.Ordinal))
            db.SystemCompanies.Add(new() { Platform = row, CompanyKey = key });
    }
    public async Task<bool> HasDependentsAsync(int id, CancellationToken ct)
    {
        // Deleting a system must never cascade away catalog or installation data.
        var entity = db.Model.FindEntityType(typeof(PlatformEntity))!;
        foreach (var fk in entity.GetReferencingForeignKeys())
        {
            if (fk.DeclaringEntityType.ClrType == typeof(SystemCompanyEntity))
                continue;
            if (fk.Properties.Count != 1)
                return true;
            var method = typeof(SystemReferenceRepository).GetMethod(nameof(HasDependentAsync), BindingFlags.Instance | BindingFlags.NonPublic)!.MakeGenericMethod(fk.DeclaringEntityType.ClrType);
            if (await (Task<bool>)method.Invoke(this, [fk.Properties[0].Name, id, ct])!)
                return true;
        }
        return false;
    }
    private Task<bool> HasDependentAsync<T>(string property, int id, CancellationToken ct) where T : class => db.Set<T>().AnyAsync(x => EF.Property<int?>(x, property) == id, ct);
    public async Task DeleteAsync(int id, CancellationToken ct)
    {
        await db.SystemCompanies.Where(x => x.PlatformId == id).ExecuteDeleteAsync(ct);
        await db.Platforms.Where(x => x.Id == id).ExecuteDeleteAsync(ct);
    }
}
public sealed class CompanyReferenceRepository : ICompanyReferenceRepository
{
    private readonly RomdDbContext db;

    public CompanyReferenceRepository(RomdDbContext db)
    {
        this.db = db;
    }

    public async Task<CompanyReference?> GetAsync(string key, CancellationToken ct) =>
        await db.Companies.SingleOrDefaultAsync(x => x.Key == key, ct) is { } row ? new(row.Key, new(row.Ownership, row.BuiltInVersion), TypedReferenceMapping.Definition(row), row.Overrides) : null;
    public async Task<bool> AllExistAsync(IReadOnlyList<string> keys, CancellationToken ct) => await db.Companies.CountAsync(x => keys.Contains(x.Key), ct) == keys.Distinct(StringComparer.Ordinal).Count();
    public async Task StageAsync(CompanyReference company, CancellationToken ct)
    {
        var row = await db.Companies.AsTracking().SingleOrDefaultAsync(x => x.Key == company.Key, ct);
        if (row is null)
        { row = new() { Key = company.Key, Name = company.Effective.Name }; db.Companies.Add(row); }
        row.Ownership = company.Metadata.Ownership;
        row.BuiltInVersion = company.Metadata.BuiltInVersion;
        row.BaseName = company.Definition.Name;
        row.BaseDescription = company.Definition.Description;
        row.Retired = company.Definition.Retired;
        row.Overrides = company.Overrides;
    }
    public Task<bool> HasDependentsAsync(string key, CancellationToken ct) => db.SystemCompanies.AnyAsync(x => x.CompanyKey == key, ct);
    public async Task DeleteAsync(string key, CancellationToken ct) => await db.Companies.Where(x => x.Key == key).ExecuteDeleteAsync(ct);
}
