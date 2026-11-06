using ErrorOr;
using Romd.Admin.Application.Source.Rom;
using Romd.Admin.Application.Source.Rom.Reconciliation;
using Romd.Domain.Source.Rom;

namespace Romd.Persistence.MediaFacts;

/// <summary>Publishes immutable raw facts and their general subject demand in the same caller transaction.</summary>
public sealed class RomMediaObservationStore : IRomMediaObservationStore
{
    private readonly RomdDbContext _context;
    private readonly IMediaReconciliationRequests _requests;
    public RomMediaObservationStore(RomdDbContext context, IMediaReconciliationRequests requests)
    {
        this._context = context;
        this._requests = requests;
    }
    public async Task<ErrorOr<Success>> StageAsync(int romId, RomRetainedMeasurement retained, RomLogicalInspection observation, CancellationToken ct = default)
    {
        var result = await new LogicalInspectionFactWriter(_context).StageAsync(romId, retained, observation, ct);
        if (result.IsError)
            return result.Errors;
        if (result.Value.Created)
        {
            var accepted = await _requests.StageSubjectAsync(new(result.Value.Id, new MediaReconciliationSubject.LogicalRom(romId), false,
                MediaReconciliationCause.Facts, null, null), ct);
            if (accepted.IsError)
                return accepted.Errors;
        }
        return Result.Success;
    }
}
