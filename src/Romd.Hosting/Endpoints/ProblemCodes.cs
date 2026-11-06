namespace Romd.Host.Endpoints;

/// <summary>
///     Stable errorCode values minted by shared hosting seams
///     (docs/decisions/api-contract-policy.md, "One Error Envelope"): the request-level codes for
///     binding, routing and unhandled failures, and the area codes of shared hosting checks
///     (system keys, delivery, reference resources and VFS). Use-case errors
///     carry their application <c>*Errors</c> factory's code, and a check local to one endpoint
///     names its code where it fails.
/// </summary>
public static class ProblemCodes
{
    /// <summary>The request body failed binding (malformed JSON or form, or a missing body).</summary>
    public const string RequestInvalidBody = "Request.InvalidBody";

    /// <summary>
    ///     A route, query or header parameter failed binding or a declared constraint, or a query filter
    ///     names an unknown system key; <c>errors</c> names each parameter by its wire name.
    /// </summary>
    public const string RequestInvalidParameter = "Request.InvalidParameter";

    /// <summary>The request body exceeds the operation's size limit.</summary>
    public const string RequestPayloadTooLarge = "Request.PayloadTooLarge";

    /// <summary>No operation matches the requested /api route.</summary>
    public const string RequestRouteNotFound = "Request.RouteNotFound";

    /// <summary>The /api route exists but not for the request method.</summary>
    public const string RequestMethodNotAllowed = "Request.MethodNotAllowed";

    /// <summary>The /api operation does not accept the request content type.</summary>
    public const string RequestUnsupportedMediaType = "Request.UnsupportedMediaType";

    /// <summary>An unhandled exception; the body is sanitized.</summary>
    public const string GeneralUnhandled = "General.Unhandled";

    /// <summary>A problem written without an errorCode and without an exception.</summary>
    public const string GeneralUnexpected = "General.Unexpected";

    public const string DeliveryContentNotFound = "Delivery.ContentNotFound";

    public const string ReferenceAssetNotFound = "ReferenceAsset.NotFound";

    public const string ReferenceCatalogUnavailable = "ReferenceCatalog.Unavailable";

    public const string ReferenceResourceNotFound = "ReferenceResource.NotFound";

    /// <summary>A <c>systemKey</c> route parameter names no system.</summary>
    public const string SystemsNotFound = "Systems.NotFound";

    /// <summary>A request-body field names no system (a query filter is <see cref="RequestInvalidParameter" />).</summary>
    public const string SystemsUnknownKey = "Systems.UnknownKey";

    public const string VfsConnectionsUnavailable = "Vfs.ConnectionsUnavailable";
}
