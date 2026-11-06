using System.Buffers.Binary;
using System.Data;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore.Storage;
using Romd.Admin.Application.Source.Rom;
using Romd.Domain.Source.Rom;

namespace Romd.Persistence.Repositories;

/// <summary>Serializes every assertion writer for one complete published disc quartet through caller commit.</summary>
/// <remarks>Acquire after ROM/ordered-file locks in publication or processing. Import takes only this guard,
/// before inserting a reference, and never waits for ROM/file locks. Initialized zero CRC remains a valid key.</remarks>
internal static class RomPs2ReferenceAssertionLock
{
    internal static Task AcquireAsync(RomdDbContext context, RomDiscMediaReferenceQuery query, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(query);
        var transaction = context.Database.CurrentTransaction;
        if (transaction is null)
            throw new InvalidOperationException("PS2 reference assertion locks require the caller's transaction.");
        if (transaction.GetDbTransaction().IsolationLevel != IsolationLevel.ReadCommitted)
            throw new InvalidOperationException("PS2 reference assertion locks require the caller's ReadCommitted transaction so scans observe publications committed before the guard.");
        // Canonical network order: length(8), SHA1(20), MD5(16), initialized CRC32(4).
        // Reference assertions have no logical SHA256. Hash collisions only serialize unrelated tuples.
        Span<byte> tuple = stackalloc byte[48];
        BinaryPrimitives.WriteInt64BigEndian(tuple, query.LogicalBytes);
        query.Sha1.AsSpan().CopyTo(tuple[8..28]);
        query.Md5.AsSpan().CopyTo(tuple[28..44]);
        query.Crc32.AsSpan().CopyTo(tuple[44..48]);
        Span<byte> digest = stackalloc byte[32];
        SHA256.HashData(tuple, digest);
        int key = BinaryPrimitives.ReadInt32BigEndian(digest);
        // Family 147 owns files, 148 owns artwork completion. 149 is the disc assertion keyspace.
        return JobAcceptanceLock.AcquireAsync(context, 149, key, ct);
    }
}
