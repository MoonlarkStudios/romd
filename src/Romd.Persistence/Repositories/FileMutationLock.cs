using System.Buffers.Binary;
using Romd.Admin.Application.Storage.Files;
using Romd.Domain.Hashing;

namespace Romd.Persistence.Repositories;

public sealed class FileMutationLock(RomdDbContext context) : IFileMutationLock
{
    public IFilePublicationBoundary CapturePublicationBoundary()
    {
        var transaction = context.Database.CurrentTransaction ?? throw new InvalidOperationException(
            "File publication requires a caller-owned transaction.");
        return new PublicationBoundary(context, transaction.TransactionId);
    }

    private sealed class PublicationBoundary(RomdDbContext context, Guid transactionId) : IFilePublicationBoundary
    {
        public void EnsureActive()
        {
            if (context.Database.CurrentTransaction?.TransactionId != transactionId)
                throw new InvalidOperationException("File publication receipts belong to their original active transaction.");
        }
    }

    public Task AcquireAsync(Sha256 hash, CancellationToken ct = default)
    {
        if (context.Database.CurrentTransaction is null)
            throw new InvalidOperationException("File mutation locks require a caller-owned transaction.");
        if (hash.IsEmpty)
            throw new ArgumentException("A file hash is required.", nameof(hash));
        // Collisions serialize unrelated hashes but never weaken exclusion. The
        // family separates this keyspace from catalog and job acceptance locks.
        var key = BinaryPrimitives.ReadInt32BigEndian(hash.AsSpan());
        return JobAcceptanceLock.AcquireAsync(context, 147, key, ct);
    }
}
