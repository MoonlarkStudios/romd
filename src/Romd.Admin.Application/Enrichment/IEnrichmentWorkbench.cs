using ErrorOr;
using Romd.Application.Common.Concurrency;
using Romd.Contracts.Management.Enrichment;
using Romd.Contracts.Common.Models;
namespace Romd.Admin.Application.Enrichment;

/// <summary>Current catalog assessment. Jobs are execution history, never workspace membership.</summary>
public interface IEnrichmentWorkbench
{
    /// <summary>The largest page the operation serves; the endpoint publishes and enforces it.</summary>
    public const int MaxLimit = 100;

    Task<ErrorOr<Page<EnrichmentAssessmentDto>>> ReadAsync(string view, string? cursor, int limit, string? query, CancellationToken ct);
    Task<ErrorOr<EnrichmentWorkbenchCountsDto>> ReadCountsAsync(string? query, CancellationToken ct);
    Task<ErrorOr<EnrichmentAssessmentDto>> GetAsync(int titleId, CancellationToken ct);
    /// <summary>
    ///     Accepts or revisits an omission when <paramref name="ifMatch" /> holds the title's current revision, and
    ///     rotates it; a stale validator is 412 and writes nothing. It runs in the caller's transaction, which then
    ///     holds the rotated revision until commit.
    /// </summary>
    Task<ErrorOr<Updated>> SetOmissionAsync(int titleId, string field, bool accepted, RevisionPrecondition ifMatch, CancellationToken ct);
}
