using ErrorOr;
using Romd.Application.Common.Cqrs;
using Romd.Application.Common.Security;
using Romd.Domain.Identity;

namespace Romd.Admin.Application.Export.Queries.ResolveExportScope;

public sealed record ResolveExportScopeQuery(int? RequestedLibraryId) : IQuery<AuthorizedExportScope>;

public sealed class ResolveExportScopeQueryHandler(
    ICurrentUser currentUser,
    IExportAuthorizationReader authorizationReader)
    : IQueryHandler<ResolveExportScopeQuery, AuthorizedExportScope>
{
    public async Task<ErrorOr<AuthorizedExportScope>> HandleAsync(
        ResolveExportScopeQuery query,
        CancellationToken ct = default)
    {
        if (currentUser.UserId is not { } userId)
        {
            return ExportErrors.CurrentUserRequired();
        }

        if (currentUser.HasRole(RomdRoleType.Admin))
        {
            if (query.RequestedLibraryId is not { } requestedLibraryId)
            {
                return new AuthorizedExportScope(
                    new ExportScope.AllCatalog(),
                    EffectiveLibraryUserId: null);
            }

            var libraryScope = await authorizationReader.GetCurrentLibraryScopeAsync(requestedLibraryId, ct);
            if (libraryScope is null)
            {
                return ExportErrors.LibraryUnavailable();
            }

            ExportScope scope = libraryScope;
            return new AuthorizedExportScope(scope, EffectiveLibraryUserId: null);
        }

        var effectiveScope = await authorizationReader.GetEffectiveLibraryScopeAsync(userId, ct);
        if (effectiveScope is null)
        {
            return query.RequestedLibraryId is null
                ? ExportErrors.LibraryRequired()
                : ExportErrors.AdminRoleRequired();
        }

        if (query.RequestedLibraryId is { } requested && requested != effectiveScope.LibraryId)
        {
            return ExportErrors.AdminRoleRequired();
        }

        return new AuthorizedExportScope(effectiveScope, userId);
    }
}
