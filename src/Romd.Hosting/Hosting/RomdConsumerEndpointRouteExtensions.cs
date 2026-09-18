using Romd.Host.Endpoints;

namespace Romd.Hosting;

public static class RomdConsumerEndpointRouteExtensions
{
    public static RouteGroupBuilder MapRomdConsumerHttp(this RouteGroupBuilder api)
    {
        api.MapConsumerServerEndpoints();
        api.MapConsumerAccessEndpoints();
        api.MapConsumerAccountEndpoints();
        api.MapConsumerIdentityEndpoints();
        api.MapConsumerBrowseEndpoints();
        api.MapConsumerCollectionEndpoints();
        api.MapConsumerDeliveryEndpoints();
        api.MapConsumerPlaybackEndpoints();
        api.MapConsumerPlayActivityEndpoints();

        api.MapReferenceCatalogEndpoints(admin: false);
        return api;
    }
}
