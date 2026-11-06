using Romd.Host.Endpoints;

namespace Romd.Hosting;

public static class RomdAdminEndpointRouteExtensions
{
    public static RouteGroupBuilder MapRomdAdminHttp(this RouteGroupBuilder api)
    {
        // Any admin route that fails to declare its own authorization decision is denied for
        // anonymous callers (docs/decisions/api-contract-policy.md, "Protected By Default").
        // The authorization-inventory test additionally requires every /api route to carry a
        // named policy or an explicit AllowAnonymous, so this fallback never decides silently.
        api.RequireAuthorization();
        api.WithRomdParameterValidation();

        api.MapAuthEndpoints();
        api.MapUserEndpoints();
        api.MapLibraryEndpoints();
        api.MapUploadEndpoints();
        api.MapJobEndpoints();
        api.MapDatEndpoints();
        api.MapAdminDiscSetEndpoints();
        api.MapAdminOriginalDiscLayoutEndpoints();
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
        api.MapDatCatalogEnrollmentEndpoints();
        api.MapAdminEndpoints();
        api.MapMediaReconciliationEndpoints();
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
