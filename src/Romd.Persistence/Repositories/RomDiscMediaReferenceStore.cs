using ErrorOr;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Romd.Admin.Application.Source.Rom;
using Romd.Domain.Source.Rom;
using Romd.Persistence.Entities;

namespace Romd.Persistence.Repositories;

/// <summary>Read-only exact published-identity lookup. Admission and raw retrieval run outside the media lease.</summary>
public sealed class RomDiscMediaReferenceStore : IRomDiscMediaReferenceStore
{
    public const int MaximumResults = 129;
    private readonly RomdDbContext _context;

    public RomDiscMediaReferenceStore(RomdDbContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        _context = context;
    }

    public async Task<ErrorOr<IReadOnlyList<RomDiscMediaReferenceRecord>>> FindAsync(RomDiscMediaReferenceQuery query,
        int maximumResults, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (maximumResults is < 1 or > MaximumResults)
            throw new ArgumentOutOfRangeException(nameof(maximumResults));
        ct.ThrowIfCancellationRequested();
        uint crc32 = query.Crc32.ToUInt32();
        List<RomDiscMediaReferenceEntity> rows;
        try
        {
            rows = await _context.Set<RomDiscMediaReferenceEntity>().AsNoTracking()
                .Where(row => row.LogicalBytes == query.LogicalBytes && row.Sha1 == query.Sha1 &&
                    row.Md5 == query.Md5 && row.Crc32 == crc32)
                .OrderBy(row => row.Id).Take(maximumResults).ToListAsync(ct);
        }
        catch (NpgsqlException error) when (error.IsTransient)
        {
            ct.ThrowIfCancellationRequested();
            return RomDiscMediaReferenceErrors.Unavailable();
        }
        catch (TimeoutException)
        {
            ct.ThrowIfCancellationRequested();
            return RomDiscMediaReferenceErrors.Unavailable();
        }
        // Npgsql EF 10.0.3 wraps transient query failures. Match its pinned wrapper narrowly;
        // unrelated logic exceptions and corrupt-row rehydration must still propagate.
        catch (InvalidOperationException error) when (
            error.Message == "An exception has been raised that is likely due to a transient failure." &&
            error.InnerException is NpgsqlException { IsTransient: true } or TimeoutException)
        {
            ct.ThrowIfCancellationRequested();
            return RomDiscMediaReferenceErrors.Unavailable();
        }
        ct.ThrowIfCancellationRequested();
        // Rehydration is outside the transient-I/O catches: corrupt rows and logic bugs must propagate.
        IReadOnlyList<RomDiscMediaReferenceRecord> records = rows.Select(row => row.ToDomain()).ToArray();
        return ErrorOrFactory.From(records);
    }
}
