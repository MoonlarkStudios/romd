namespace Romd.Host.Endpoints;

internal static class ContentCapacityResponse
{
    public static IResult Unavailable(HttpContext context)
    {
        context.Response.Headers.RetryAfter = "15";
        context.Response.Headers.CacheControl = "private, no-store";
        return ProblemResults.Problem(StatusCodes.Status503ServiceUnavailable,
            "Content.Capacity", "Content decoding capacity is occupied; retry later.");
    }
}
