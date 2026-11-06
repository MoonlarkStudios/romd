using ErrorOr;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Romd.Admin.Application.Source.Rom;
using Romd.Admin.Application.Source.Rom.Reconciliation;
using Romd.Domain.Source.Rom;
using Romd.Persistence.Entities;

namespace Romd.Persistence.Repositories;

/// <summary>Stages an immutable admitted retrieval within the caller's publication transaction.</summary>
/// <remarks>No source authentication, fetch, nested transaction, flush or commit belongs here.
/// A Guid replays only the complete retained record and raw bytes; other retrievals remain separate.
/// The shared tuple guard precedes insertion; matching retained boot wakeups share this caller transaction.</remarks>
public sealed class RomDiscMediaReferenceImportStore : IRomDiscMediaReferenceImportStore
{
    private readonly RomdDbContext _context;
    private readonly IMediaReconciliationRequests _requests;

    public RomDiscMediaReferenceImportStore(RomdDbContext context, IMediaReconciliationRequests requests)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(requests);
        _context = context;
        _requests = requests;
    }

    public async Task<string> GetRuntimeRoleAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        string role = await _context.Database.SqlQueryRaw<string>("SELECT current_user AS \"Value\"").SingleAsync(ct);
        ct.ThrowIfCancellationRequested();
        return role;
    }

    public async Task<ErrorOr<RomDiscMediaReferenceRecord>> StageAsync(RomDiscMediaReferenceRecord record,
        ReadOnlyMemory<byte> rawBody, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(record);
        if (_context.Database.CurrentTransaction is null)
            throw new InvalidOperationException("Disc references require a caller-owned publication transaction.");
        // Validate and copy before the first await; no mutable caller alias enters retained custody.
        var incoming = RomDiscMediaReferenceEntity.FromDomain(record, rawBody.Span);
        byte[] retainedBody = incoming.GetRawBodySnapshot();
        var query = new RomDiscMediaReferenceQuery(record.LogicalBytes, record.Sha1, record.Md5, record.Crc32);
        try
        {
            // Lock the absent-row assertion boundary before the immutable insert, not only before fanout.
            await RomPs2ReferenceAssertionLock.AcquireAsync(_context, query, ct);
            int inserted = await _context.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO romd."RomDiscMediaReferences"
                    ("Id", "System", "Media", "FileCount", "LogicalBytes", "Sha1", "Md5", "Crc32",
                     "RawReferenceBytes", "RawReferenceSha256", "ReferenceSource", "ParserProfile",
                     "AcceptedSourceProvenance", "RetrievedAtUtcTicks", "RawBody")
                VALUES ({incoming.Id}, {incoming.System.ToString()}, {incoming.Media.ToString()}, {incoming.FileCount},
                    {incoming.LogicalBytes}, {incoming.Sha1.ToArray()}, {incoming.Md5.ToArray()}, {(long)incoming.Crc32},
                    {incoming.RawReferenceBytes}, {incoming.RawReferenceSha256.ToArray()}, {incoming.ReferenceSource},
                    {incoming.ParserProfile}, {incoming.AcceptedSourceProvenance}, {incoming.RetrievedAtUtcTicks}, {retainedBody})
                ON CONFLICT ("Id") DO NOTHING
                """, ct);
            var retained = await _context.Set<RomDiscMediaReferenceEntity>().AsNoTracking()
                .SingleOrDefaultAsync(row => row.Id == record.Id, ct);
            ct.ThrowIfCancellationRequested();
            if (retained is null)
                throw new InvalidOperationException("The staged disc reference is unavailable.");
            // Corrupt rows and mapping failures propagate; neither is a transient admission failure.
            var retainedRecord = retained.ToDomain();
            bool sameRetrieval = retainedRecord == record && retained.GetRawBodySnapshot().AsSpan().SequenceEqual(retainedBody);
            ct.ThrowIfCancellationRequested();
            if (!sameRetrieval)
                return RomDiscMediaReferenceImportErrors.ConflictingIdentity();
            if (inserted == 1)
            {
                // The tuple assertion lock also covers absent matches. A later observation stages its own demand.
                // Positive and contradictory retained facts both need a fresh computed decision.
                int[] affected = await _context.RomFiles.Where(row => row.ContentSize == query.LogicalBytes &&
                    row.Sha1 == query.Sha1 && row.Md5 == query.Md5 && row.Crc32 == query.Crc32).OrderBy(row => row.Id).Select(row => row.Id).ToArrayAsync(ct);
                foreach (int romId in affected)
                {
                    var accepted = await _requests.StageSubjectAsync(new(Guid.NewGuid(), new MediaReconciliationSubject.LogicalRom(romId), false,
                        MediaReconciliationCause.Facts, null, null), ct);
                    if (accepted.IsError)
                        return accepted.Errors;
                }
            }
            ct.ThrowIfCancellationRequested();
            return ErrorOrFactory.From(retainedRecord);
        }
        catch (NpgsqlException failure) when (failure.IsTransient)
        {
            ct.ThrowIfCancellationRequested();
            return RomDiscMediaReferenceImportErrors.Unavailable();
        }
        catch (TimeoutException)
        {
            ct.ThrowIfCancellationRequested();
            return RomDiscMediaReferenceImportErrors.Unavailable();
        }
        // Npgsql EF 10.0.3 wraps transient query failures. Match its pinned wrapper narrowly;
        // unrelated logic exceptions and corrupt-row rehydration must still propagate.
        catch (InvalidOperationException failure) when (
            failure.Message == "An exception has been raised that is likely due to a transient failure." &&
            failure.InnerException is NpgsqlException { IsTransient: true } or TimeoutException)
        {
            ct.ThrowIfCancellationRequested();
            return RomDiscMediaReferenceImportErrors.Unavailable();
        }
    }
}
