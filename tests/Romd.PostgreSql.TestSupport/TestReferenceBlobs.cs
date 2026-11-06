using System.Collections.Concurrent;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Romd.Application.Common.ReferenceCatalog;
using Romd.Domain.Hashing;
using Romd.Persistence;
using Romd.Persistence.Entities;

namespace Romd.PostgreSql.TestSupport;

/// <summary>
///     Deterministic blob IO for persistence tests. Publication records file rows on the test's context, as
///     the ledger does, without a content store; CAS lifecycle tests use the real store and ledger.
/// </summary>
public sealed class TestReferenceBlobs(RomdDbContext db) : IReferenceBlobStore
{
    private static readonly ConcurrentDictionary<string, byte[]> Contents = new();

    public static TestReferenceBlobs For(RomdDbContext db) => new(db);

    public async Task<IStagedReferencePublication> PublishStagedAsync(IReadOnlyList<byte[]> contents, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var transaction = db.Database.CurrentTransaction
            ?? throw new InvalidOperationException("Reference test publication requires the caller's transaction.");
        var ownedContents = contents.Select(bytes => bytes.ToArray()).ToArray();
        var rows = new List<FileEntityPersistence>(contents.Count);
        foreach (byte[] bytes in ownedContents)
        {
            ct.ThrowIfCancellationRequested();
            byte[] hash = SHA256.HashData(bytes);
            Contents[Convert.ToHexStringLower(hash)] = bytes.ToArray();
            var sha256 = Sha256.FromSpan(hash);
            var file = db.Files.Local.FirstOrDefault(row => row.Sha256 == sha256)
                ?? await db.Files.AsTracking().SingleOrDefaultAsync(x => x.Sha256 == sha256, ct);
            if (file is null)
            {
                file = new FileEntityPersistence
                {
                    Sha256 = sha256,
                    Size = bytes.Length,
                    SizeOnDisk = bytes.Length,
                    CreatedAt = DateTimeOffset.UtcNow
                };
                db.Files.Add(file);
            }
            rows.Add(file);
        }
        return new Publication(db, transaction.TransactionId, rows);
    }

    public Task<byte[]?> ReadAsync(string hash, CancellationToken ct) => Task.FromResult(Contents.GetValueOrDefault(hash));

    private sealed class Publication(RomdDbContext db, Guid transactionId, IReadOnlyList<FileEntityPersistence> rows)
        : IStagedReferencePublication
    {
        private bool _disposed;
        public bool RequiresFlush
        {
            get
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (db.Database.CurrentTransaction?.TransactionId != transactionId
                    || rows.Any(row => db.Entry(row).State is EntityState.Detached or EntityState.Deleted))
                    throw new InvalidOperationException("Reference test receipts belong to their original active transaction.");
                return rows.Any(row => db.Entry(row).State == EntityState.Added);
            }
        }
        public IReadOnlyList<int> ReadAfterFlush()
        {
            if (RequiresFlush || rows.Any(row => row.Id <= 0))
                throw new InvalidOperationException("Flush reference test publication before reading identities.");
            return rows.Select(row => row.Id).ToArray();
        }
        public ValueTask DisposeAsync() { _disposed = true; return ValueTask.CompletedTask; }
    }
}
