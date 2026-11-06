using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Romd.Application.Common.ReferenceCatalog;
using Romd.Contracts.Common.ReferenceCatalog;
using Romd.Persistence.Entities;

namespace Romd.Persistence.ReferenceData;

public sealed partial class ReferenceCatalogService : IReferenceCatalogService
{
    private readonly RomdDbContext db;

    private readonly IReferenceBlobStore blobs;
    public ReferenceCatalogService(RomdDbContext db, IReferenceBlobStore blobs) { this.db = db; this.blobs = blobs; }

    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    internal static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    internal static Task LockAsync(RomdDbContext db, CancellationToken ct) => db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(82419001)", ct);
    private static bool AssetHash(string value) => Regex.IsMatch(value, "^[a-f0-9]{64}$");

    public Task<Romd.Application.Common.Systems.SystemKeys> GetSystemKeysAsync(CancellationToken ct) => new Romd.Persistence.Queries.SystemSummaryReader(db).ReadKeysAsync(ct);
    public Task<int?> ResolveSystemIdAsync(string key, CancellationToken ct) => db.Platforms.Where(x => x.Ownership != null && x.CanonicalKey == key).Select(x => (int?)x.Id).SingleOrDefaultAsync(ct);

    public async Task<ReferenceCatalogDto?> GetCurrentAsync(CancellationToken ct)
    {
        var text = await db.ReferenceCatalogState.Select(x => x.Json).SingleOrDefaultAsync(ct);
        return text is null ? null : JsonSerializer.Deserialize<ReferenceCatalogDto>(text, Json);
    }
    public async Task<ReferenceAssetContent?> GetAssetAsync(string hash, CancellationToken ct)
    {
        if (!AssetHash(hash))
            return null;
        var type = await db.ReferenceAssets.Where(x => x.Hash == hash).Select(x => x.ContentType).SingleOrDefaultAsync(ct);
        if (type is null)
            return null;
        var bytes = await blobs.ReadAsync(hash, ct);
        return bytes is null ? null : new ReferenceAssetContent(bytes, type);
    }
    /// <summary>Stages the reference to published content and renews its reservation.</summary>
    internal static async Task<string> RecordAssetAsync(RomdDbContext db, byte[] bytes, int fileId, string contentType, CancellationToken ct)
    {
        var hash = Hash(bytes);
        var asset = db.ReferenceAssets.Local.FirstOrDefault(x => x.Hash == hash)
            ?? await db.ReferenceAssets.AsTracking().SingleOrDefaultAsync(x => x.Hash == hash, ct);
        if (asset is null)
            db.ReferenceAssets.Add(new ReferenceAssetEntity { Hash = hash, FileId = fileId, ContentType = contentType, ReservedUntil = DateTimeOffset.UtcNow.AddDays(7) });
        else
            asset.ReservedUntil = DateTimeOffset.UtcNow.AddDays(7);
        return hash;
    }
}
