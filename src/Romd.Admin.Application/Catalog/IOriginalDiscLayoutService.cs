using ErrorOr;
using Romd.Application.Common.Concurrency;
using Romd.Contracts.Common.Models;
using Romd.Contracts.Management.Models;

namespace Romd.Admin.Application.Catalog;

public interface IOriginalDiscLayoutService
{
    Task<ErrorOr<Page<OriginalDiscLayoutDto>>> ListAsync(int? platformId, string? cursor, int limit, CancellationToken ct);
    Task<ErrorOr<OriginalDiscLayoutDto>> GetAsync(int id, CancellationToken ct);
    Task<ErrorOr<Page<OriginalDiscLayoutCandidateDto>>> CandidatesAsync(int id, int? platformId, string? cursor, int limit, CancellationToken ct);
    Task<ErrorOr<Page<OriginalDiscLayoutAuditDto>>> AuditAsync(int id, string? cursor, int limit, CancellationToken ct);
    /// <summary>Stage the decision and return every changed layout ID, including any retired replacement, in the caller transaction.</summary>
    Task<ErrorOr<IReadOnlyList<int>>> DecideStagedAsync(int id, OriginalDiscLayoutDecisionRequest request, RevisionPrecondition precondition, Guid actor, CancellationToken ct);
}
