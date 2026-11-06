using ErrorOr;
using Romd.Admin.Application.MetadataProviders;
using Romd.Application.Common.Concurrency;
using Romd.Application.Common.Ids;
using Romd.Contracts.Management.MetadataProviders;
using Romd.Host.Authorization;

namespace Romd.Host.Endpoints;

/// <summary>
///     A title's provider matches. Each match is versioned on its own: its derived revision is its ETag, and every
///     write to it requires that ETag in <c>If-Match</c> and answers the match with its new one
///     (docs/decisions/api-contract-policy.md, "Idempotency And Concurrency"). A write also rotates the title's
///     revision, and set and confirm expose the enrichment that applies
///     the match as <c>pendingJobId</c> (docs/decisions/api-contract-policy.md, "Async Operations And Jobs"). A
///     free-text search names no title, so it lives under the provider.
/// </summary>
public static class ProviderMatchEndpoints
{
    /// <summary>The errorCode area of provider-match preconditions.</summary>
    internal const string PreconditionArea = "ProviderMatch";

    /// <summary>
    ///     Maps the routes. An unknown provider is 404 and a disabled or unconfigured one 409. Setting and searching
    ///     reach the provider, so they also answer an unavailable provider (503) and an invalid provider response (502),
    ///     and an id, URL or query the provider rejects is a 400 keyed by its field.
    /// </summary>
    public static IEndpointRouteBuilder MapProviderMatchEndpoints(this IEndpointRouteBuilder app)
    {
        var matches = app.MapGroup("/titles/{titleId}/provider-matches").WithTags("ProviderMatches")
            .RequireAuthorization(AuthorizationPolicies.RequireManager);
        matches.MapGet("", List).WithName("ListTitleProviderMatches")
            .Produces<IReadOnlyList<TitleProviderMatchDto>>()
            .ProducesProblems(StatusCodes.Status404NotFound);
        // The derived revision does not cover the live game preview, availability or background decisions, so the
        // GET emits the validator without revalidating.
        matches.MapGet("{providerId}", Get).WithName("GetTitleProviderMatch")
            .Produces<TitleProviderMatchDto>()
            .ETagOnlyRead()
            .ProducesProblems(StatusCodes.Status404NotFound, StatusCodes.Status409Conflict);
        matches.MapPut("{providerId}", Replace).WithName("ReplaceTitleProviderMatch")
            .WithDescription("Match the title to a provider game, chosen by its id or page URL")
            .Produces<TitleProviderMatchDto>()
            .ConditionalWrite()
            .ProducesProblems(StatusCodes.Status400BadRequest, StatusCodes.Status404NotFound, StatusCodes.Status409Conflict,
                StatusCodes.Status502BadGateway, StatusCodes.Status503ServiceUnavailable);
        matches.MapPost("{providerId}/confirm", Confirm).WithName("ConfirmTitleProviderMatch")
            .WithDescription("Confirm the automatic match, making it immutable to automatic matching")
            .Produces<TitleProviderMatchDto>()
            .ConditionalWrite()
            .ProducesProblems(StatusCodes.Status404NotFound, StatusCodes.Status409Conflict);
        matches.MapDelete("{providerId}", Unlink).WithName("DeleteTitleProviderMatch")
            .WithDescription("Unlink the provider and suppress automatic matching for this title, answering the unlinked match")
            .Produces<TitleProviderMatchDto>()
            .ConditionalWrite()
            .ProducesProblems(StatusCodes.Status404NotFound, StatusCodes.Status409Conflict);

        var providers = app.MapGroup("/metadata-providers").WithTags("ProviderMatches")
            .RequireAuthorization(AuthorizationPolicies.RequireManager);
        providers.MapGet("{providerId}/games", Search).WithName("SearchMetadataProvider")
            .WithDescription("Bounded live provider game suggestions, not a catalog enumeration. IGDB reads at most " +
                "five 100-record responses (500 total), 2 MiB each, within 10 seconds. A full final response is " +
                "BudgetLimited. SteamGridDB reads at most 100 raw autocomplete records, 2 MiB, within 30 seconds " +
                "and is always ProviderLimited, even when empty. Complete describes only observed query exhaustion; " +
                "there is no snapshot guarantee. Incomplete results require a narrower query or explicit id/URL resolution.")
            .Produces<ProviderGameSearchResultDto>()
            .ProducesProblems(StatusCodes.Status404NotFound, StatusCodes.Status409Conflict,
                StatusCodes.Status502BadGateway, StatusCodes.Status503ServiceUnavailable);
        providers.MapGet("{providerId}/game-resolution", Resolve).WithName("ResolveMetadataProviderGame")
            .WithDescription("Resolve exactly one provider game by its id or page URL, independently of free-text suggestions")
            .Produces<ProviderGameDto>()
            .ProducesProblems(StatusCodes.Status404NotFound, StatusCodes.Status409Conflict,
                StatusCodes.Status502BadGateway, StatusCodes.Status503ServiceUnavailable);
        return app;
    }

    private static async Task<IResult> List(Sqid titleId, ITitleProviderMatchService service, CancellationToken ct) =>
        (await service.GetAsync(titleId.Value, ct)).Match(Results.Ok, errors => errors.ToProblem());

    private static async Task<IResult> Get(Sqid titleId, string providerId, HttpContext context,
        ITitleProviderMatchService service, CancellationToken ct) =>
        Tagged(context, await service.GetAsync(titleId.Value, providerId, ct));

    private static Task<IResult> Replace(Sqid titleId, string providerId, SetProviderMatchRequest request,
        HttpContext context, ITitleProviderMatchService service, CancellationToken ct) =>
        WriteAsync(context, ifMatch => service.SetAsync(titleId.Value, providerId, request.ExternalId, ifMatch, ct));

    private static Task<IResult> Confirm(Sqid titleId, string providerId, HttpContext context,
        ITitleProviderMatchService service, CancellationToken ct) =>
        WriteAsync(context, ifMatch => service.ConfirmAsync(titleId.Value, providerId, ifMatch, ct));

    private static Task<IResult> Unlink(Sqid titleId, string providerId, HttpContext context,
        ITitleProviderMatchService service, CancellationToken ct) =>
        WriteAsync(context, ifMatch => service.UnlinkAsync(titleId.Value, providerId, ifMatch, ct));

    private static async Task<IResult> Search(string providerId, string query, ITitleProviderMatchService service,
        CancellationToken ct) =>
        (await service.SearchAsync(providerId, query, ct)).Match(Results.Ok, errors => errors.ToProblem());

    private static async Task<IResult> Resolve(string providerId, string input, ITitleProviderMatchService service,
        CancellationToken ct) =>
        (await service.ResolveAsync(providerId, input, ct)).Match(Results.Ok, errors => errors.ToProblem());

    private static async Task<IResult> WriteAsync(HttpContext context,
        Func<RevisionPrecondition, Task<ErrorOr<TitleProviderMatchDto>>> write)
    {
        var ifMatch = Preconditions.IfMatch(context, PreconditionArea);
        return ifMatch.IsError ? ifMatch.Errors.ToProblem() : Tagged(context, await write(ifMatch.Value));
    }

    /// <summary>The match stamped with its own validator.</summary>
    private static IResult Tagged(HttpContext context, ErrorOr<TitleProviderMatchDto> result) =>
        result.Match(match => Results.Ok(match).WithETag(context, match.Revision.ToString()),
            errors => errors.ToProblem());
}
