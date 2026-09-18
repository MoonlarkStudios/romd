using Romd.Host.Endpoints;

namespace Romd.Hosting;

public static class RomdAdminEndpointRouteExtensions
{
    public static RouteGroupBuilder MapRomdAdminHttp(this RouteGroupBuilder api)
    {
        // Protected by default (docs/decisions/admin-api-contract-policy.md): any admin route
        // that fails to declare its own authorization decision is denied for anonymous callers.
        // The authorization-inventory test additionally requires every /api route to carry a
        // named policy or an explicit AllowAnonymous, so this fallback never decides silently.
        api.RequireAuthorization();

        api.MapAuthEndpoints();
        api.MapUserEndpoints();
        api.MapLibraryEndpoints();
        api.MapUploadEndpoints();
        api.MapJobEndpoints();
        api.MapDatEndpoints();
        api.MapRomEndpoints();
        api.MapPlatformEndpoints();
        api.MapSystemSetupEndpoints();
        api.MapTitleEndpoints();
        api.MapTrackedCollectionEndpoints();
        api.MapCollectionEndpoints();
        api.MapEnrichmentEndpoints();
        api.MapExportEndpoints();
        api.MapSearchEndpoints();
        api.MapCatalogEndpoints();
        api.MapTaxonomyEndpoints();
        api.MapDatCatalogEnrollmentEndpoints();
        api.MapAdminEndpoints();
        api.MapMetadataProviderEndpoints();
        api.MapArtworkCurationEndpoints();
        api.MapProviderMatchEndpoints();
        api.MapDashboardEndpoints();
        api.MapSystemEndpoints();
        api.MapDiagnosticsEndpoints();

        api.MapReferenceCatalogEndpoints(admin: true);
        return api;
    }
}
