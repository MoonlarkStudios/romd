using Romd.Host.Endpoints;

namespace Romd.Hosting;

public static class RomdConsumerEndpointRouteExtensions
{
    public static RouteGroupBuilder MapRomdConsumerHttp(this RouteGroupBuilder api)
    {
        // Protected by default (docs/decisions/api-contract-policy.md, "Protected By Default"):
        // any consumer route that fails to declare its own authorization decision is denied for
        // anonymous callers. The authorization-inventory test additionally requires every /api
        // route to carry a named policy or an explicit AllowAnonymous, so this fallback never
        // decides silently.
        api.RequireAuthorization();
        api.WithRomdParameterValidation();

        api.MapConsumerServerEndpoints();
        api.MapConsumerAccessEndpoints();
        api.MapConsumerAccountEndpoints();
        api.MapConsumerVfsConnectionEndpoints();
        api.MapConsumerIdentityEndpoints();
        api.MapConsumerBrowseEndpoints();
        api.MapConsumerCollectionEndpoints();
        api.MapConsumerDeliveryEndpoints();
        api.MapConsumerDiscSetEndpoints();
        api.MapConsumerPlaybackEndpoints();
        api.MapConsumerPlayActivityEndpoints();

        api.MapReferenceCatalogEndpoints(admin: false);
        return api;
    }
}
