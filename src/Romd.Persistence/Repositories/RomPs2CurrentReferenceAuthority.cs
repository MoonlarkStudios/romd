using System.Data;
using ErrorOr;
using Microsoft.EntityFrameworkCore.Storage;
using Romd.Admin.Application.Source.Rom;
using Romd.Domain.Source.Rom;

namespace Romd.Persistence.Repositories;

/// <summary>Serializes current-reference authority against imports of the same measured logical tuple.</summary>
public sealed class RomPs2CurrentReferenceAuthority : IRomPs2CurrentReferenceAuthority
{
    private readonly RomdDbContext _context;
    private readonly RomPs2DvdEligibilityResolver _resolver;

    public RomPs2CurrentReferenceAuthority(RomdDbContext context, RomPs2DvdEligibilityResolver resolver)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(resolver);
        _context = context;
        _resolver = resolver;
    }

    public async Task<ErrorOr<RomPs2DvdEligibilityResolution>> ResolveAsync(RomLogicalIdentity measurement,
        RomPs2BootLayoutFacts boot, DateTimeOffset inspectedAt, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(measurement);
        ArgumentNullException.ThrowIfNull(boot);
        var transaction = _context.Database.CurrentTransaction;
        if (transaction is null)
            throw new InvalidOperationException("Current PS2 authority requires a caller-owned publication transaction.");
        if (transaction.GetDbTransaction().IsolationLevel != IsolationLevel.ReadCommitted)
            throw new InvalidOperationException("Current PS2 authority requires a caller-owned ReadCommitted publication transaction so reference queries observe imports committed before the assertion guard.");
        if (measurement.Sha256.IsEmpty || measurement.Sha1.IsEmpty || measurement.Md5.IsEmpty || measurement.Crc32.IsEmpty ||
            measurement.Size is <= 0 or > RomPs2BootLayoutFacts.MaximumLogicalBytes || measurement.Size % 2048 != 0 ||
            boot.LogicalBytes != measurement.Size)
            return RomDiscMediaReferenceErrors.InvalidMeasurement();

        var query = new RomDiscMediaReferenceQuery(measurement.Size, measurement.Sha1, measurement.Md5, measurement.Crc32);
        await RomPs2ReferenceAssertionLock.AcquireAsync(_context, query, ct);
        return await _resolver.ResolveAsync(measurement, boot, inspectedAt, ct);
    }
}
