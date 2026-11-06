using ErrorOr;
using Romd.Application.Common.Cqrs;
using Romd.Application.Common.Security;
using Romd.Admin.Application.Ingestion.Jobs;
using Romd.Domain.Identity;
using Romd.Domain.Jobs;

namespace Romd.Admin.Application.Export.Queries.AuthorizeExportDownload;

public sealed record AuthorizeExportDownloadQuery(Guid JobId) : IQuery<ExportJob>;

public sealed class AuthorizeExportDownloadQueryHandler(
    IJobRepository<ExportJob> jobRepository,
    ICurrentUser currentUser,
    IExportAuthorizationReader authorizationReader)
    : IQueryHandler<AuthorizeExportDownloadQuery, ExportJob>
{
    public async Task<ErrorOr<ExportJob>> HandleAsync(
        AuthorizeExportDownloadQuery query,
        CancellationToken ct = default)
    {
        var job = await jobRepository.GetByIdAsync(query.JobId, ct);
        if (job is null || !job.IsTerminal || job.ExportPath is null)
        {
            return ExportErrors.NotFound();
        }

        if (currentUser.HasRole(RomdRoleType.Admin))
        {
            return job;
        }

        if (currentUser.UserId is not { } userId ||
            job.CreatedByUserId != userId ||
            job.ScopeKind is not ExportScopeKind.Library ||
            job.LibraryId is not { } originalLibraryId ||
            job.AuthorizedMaterializationGeneration is not { } originalGeneration)
        {
            return ExportErrors.AccessDenied();
        }

        var currentScope = await authorizationReader.GetEffectiveLibraryScopeAsync(userId, ct);
        return currentScope?.LibraryId == originalLibraryId &&
               currentScope.MaterializationGeneration == originalGeneration
            ? job
            : ExportErrors.AccessDenied();
    }
}
