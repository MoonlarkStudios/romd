using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using ErrorOr;
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
    private static Error Invalid(string message) => Error.Validation("ReferenceCatalog.Invalid", message);
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
        if (!AssetHash(hash)) return null;
        var type = await db.ReferenceAssets.Where(x => x.Hash == hash).Select(x => x.ContentType).SingleOrDefaultAsync(ct);
        if (type is null) return null;
        var bytes = await blobs.ReadAsync(hash, ct);
        return bytes is null ? null : new ReferenceAssetContent(bytes, type);
    }
    public async Task<ErrorOr<string>> AddAssetAsync(byte[] bytes, string contentType, CancellationToken ct)
    {
        // Uploaded icons are raster images. SVG is accepted only from reviewed built-in assets.
        if (bytes.Length is < 12 or > 2097152) return Invalid("Icons must be at most 2 MiB.");
        var png = bytes.AsSpan().StartsWith(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
        var webp = bytes.AsSpan(0, 4).SequenceEqual("RIFF"u8) && bytes.AsSpan(8, 4).SequenceEqual("WEBP"u8);
        if (!(contentType == "image/png" && png || contentType == "image/webp" && webp)) return Invalid("Use a PNG or WebP icon.");
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await LockAsync(db, ct);
        var hash = await StoreAssetAsync(db, blobs, bytes, contentType, ct);
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
        return hash;
    }
    internal static async Task<string> StoreAssetAsync(RomdDbContext db, IReferenceBlobStore blobs, byte[] bytes, string contentType, CancellationToken ct)
    {
        var hash = Hash(bytes);
        var digest = Romd.Domain.Hashing.Sha256.Parse(hash);
        await new Romd.Persistence.Repositories.FileMutationLock(db).AcquireAsync(digest, ct);
        var stored = await blobs.StoreAsync(bytes, ct);
        var file = await db.Files.SingleOrDefaultAsync(x => x.Sha256 == digest, ct);
        if (file is null)
        {
            file = new FileEntityPersistence { Sha256 = digest, Size = stored.Size, SizeOnDisk = stored.SizeOnDisk, IsCompressed = stored.IsCompressed, CreatedAt = DateTimeOffset.UtcNow };
            db.Files.Add(file);
            await db.SaveChangesAsync(ct);
        }
        var asset = db.ReferenceAssets.Local.FirstOrDefault(x => x.Hash == hash)
            ?? await db.ReferenceAssets.AsTracking().SingleOrDefaultAsync(x => x.Hash == hash, ct);
        if (asset is null)
            db.ReferenceAssets.Add(new ReferenceAssetEntity { Hash = hash, FileId = file.Id, ContentType = contentType, ReservedUntil = DateTimeOffset.UtcNow.AddDays(7) });
        else asset.ReservedUntil = DateTimeOffset.UtcNow.AddDays(7);
        return hash;
    }
}
