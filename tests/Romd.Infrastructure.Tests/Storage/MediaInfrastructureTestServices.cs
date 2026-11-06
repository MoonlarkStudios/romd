using Microsoft.Extensions.Logging.Abstractions;
using System.Buffers.Binary;
using ErrorOr;
using Romd.Admin.Application.Hashing;
using Romd.Admin.Application.Source.Rom;
using Romd.Domain.Source.Rom;
using Romd.Admin.Application.Common.Persistence;
using Romd.Admin.Application.Storage.Files;
using Romd.Infrastructure.Realtime;
using Romd.Infrastructure.Storage;
using Romd.Persistence;
using Romd.Persistence.Repositories;
using Romd.Storage;

namespace Romd.Infrastructure.Tests.Storage;

internal static partial class MediaInfrastructureTestServices
{
    // Managed-only fixture composition is explicit about absent native assets and reference authority.
    internal static RomPreparationService ManagedPreparation(IHashingService hashing, Func<RvzVerifier> rvz,
        IRomDiscMediaReferenceStore references) => new(new RomPreparationAdapterRegistry(hashing, rvz,
            () => new RomOriginalMediaIdentifier(rvz()),
            () => throw new NotSupportedException("This managed fixture has no native DVD assets."),
            () => throw new NotSupportedException("This managed fixture has no native CD assets.")), new RomPs2DvdEligibilityResolver(references));

    internal sealed class EmptyReferences : IRomDiscMediaReferenceStore
    {
        public Task<ErrorOr<IReadOnlyList<RomDiscMediaReferenceRecord>>> FindAsync(RomDiscMediaReferenceQuery query,
            int maximumResults, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult<ErrorOr<IReadOnlyList<RomDiscMediaReferenceRecord>>>(Array.Empty<RomDiscMediaReferenceRecord>());
        }
    }
    internal static byte[] ChdV5Prefix(uint unitBytes)
    {
        byte[] bytes = new byte[124];
        "MComprHD"u8.CopyTo(bytes);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(8), 124);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(12), 5);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(60), unitBytes);
        return bytes;
    }
    internal static FileStorageService CreateFiles(RomdDbContext db, IContentAddressableStore cas, IUnitOfWork work,
        IFileMutationLock locks, TimeProvider clock) => new(cas, new FileRepository(db), work, locks,
            new AdminRealtimeOutbox(db, clock), NullLogger<FileStorageService>.Instance, clock);

    internal static async Task<StoreResult> StoreRawChdAsync(IContentAddressableStore cas, Stream bytes, CancellationToken ct)
    {
        await using var staged = await cas.StageRawAsync(bytes, ct: ct);
        return await staged.PublishAsync(ct);
    }

}
