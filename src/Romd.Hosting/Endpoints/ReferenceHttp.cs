using ErrorOr;
using Romd.Application.Common.ReferenceCatalog;

namespace Romd.Host.Endpoints;

internal static class ReferenceHttp
{
    internal static IResult Read<T, TOverrides>(HttpContext context, ReferenceResourceResult<T, TOverrides>? result, bool overrides)
    {
        if (result is null)
            return Results.NotFound();
        context.Response.Headers.ETag = result.ETag;
        context.Response.Headers.CacheControl = "private, no-cache";
        if (context.Request.GetTypedHeaders().IfNoneMatch?.Any(x => x.Tag.Value == result.ETag || x.Tag.Value == "*") == true)
            return Results.StatusCode(304);
        return overrides ? Results.Ok(result.Overrides) : Results.Ok(result.Resource);
    }
    internal static IResult Read<T>(HttpContext context, ReferenceReadResult<T>? result)
    {
        if (result is null) return Results.NotFound();
        context.Response.Headers.ETag = result.ETag;
        context.Response.Headers.CacheControl = "private, no-cache";
        if (context.Request.GetTypedHeaders().IfNoneMatch?.Any(x => x.Tag.Value == result.ETag || x.Tag.Value == "*") == true)
            return Results.StatusCode(304);
        return Results.Ok(result.Resource);
    }
    internal static IResult Edited<T, TOverrides>(HttpContext context, ErrorOr<ReferenceResourceResult<T, TOverrides>> result, Func<T, string>? location = null, bool noContent = false)
    {
        if (result.IsError)
            return Failure(result.Errors);
        context.Response.Headers.ETag = result.Value.ETag;
        return noContent ? Results.NoContent() : location is null ? Results.Ok(result.Value.Resource) : Results.Created(location(result.Value.Resource), result.Value.Resource);
    }
    internal static IResult Failure(IReadOnlyList<Error> errors) => (int)errors[0].Type is 412 or 428
        ? ProblemResults.Problem((int)errors[0].Type, errors[0].Code, errors[0].Description) : errors.ToProblem();
}
