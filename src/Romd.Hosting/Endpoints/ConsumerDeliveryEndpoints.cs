using Romd.Application.Common.Content;
using Romd.Application.Common.Security;
using Romd.Consumer.Application;
using Romd.Consumer.Application.Delivery;
using Romd.Consumer.Application.Libraries;
using Romd.Application.Common.Cqrs;
using Romd.Application.Common.Ids;
using Romd.Consumer.Application.Delivery.Commands.IssueConsumerReleaseManifest;
using Romd.Consumer.Application.Delivery.Queries.GetConsumerPlatformBios;
using Romd.Contracts.Consumer.Delivery;
using Romd.Contracts.Consumer.Releases;
using Romd.Host.Authorization;

using ContractConsumerPlatformBios = Romd.Contracts.Consumer.Delivery.ConsumerPlatformBios;
using ContractConsumerReleaseManifest = Romd.Contracts.Consumer.Releases.ConsumerReleaseManifest;

namespace Romd.Host.Endpoints;

public static class ConsumerDeliveryEndpoints
{
    public static IEndpointRouteBuilder MapConsumerDeliveryEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("releases/{releaseId}")
            .WithTags("Consumer Delivery")
            .RequireAuthorization(AuthorizationPolicies.RequireUser);

        group.MapPost("manifest", IssueReleaseManifest)
            .WithName("IssueConsumerReleaseManifest")
            .Produces<ContractConsumerReleaseManifest>()
            .Produces(StatusCodes.Status401Unauthorized)
            .ProducesProblems(StatusCodes.Status400BadRequest, StatusCodes.Status404NotFound);

        // These authenticated routes recheck every BIN in the current library snapshot.
        group.MapGet("cd-archive", async (Sqid releaseId, ICurrentUser user, IConsumerCdArchiveRepository repository,
            IRomCdArchiveDownloadReader reader, HttpContext context, CancellationToken ct) =>
            await DownloadCdAsync(releaseId, user, repository, reader, context, false, ct))
            .WithName("DownloadConsumerCdArchive").Produces(200, contentType: "application/octet-stream").ProducesProblems(400, 404);
        group.MapGet("cd-archive/cue", async (Sqid releaseId, ICurrentUser user, IConsumerCdArchiveRepository repository,
            IRomCdArchiveDownloadReader reader, HttpContext context, CancellationToken ct) =>
            await DownloadCdAsync(releaseId, user, repository, reader, context, true, ct))
            .WithName("DownloadConsumerCdCue").Produces(200, contentType: "application/octet-stream").ProducesProblems(400, 404);

        app.MapGet("systems/{systemKey}/bios", GetPlatformBios)
            .WithTags("Consumer Delivery")
            .RequireAuthorization(AuthorizationPolicies.RequireUser)
            .WithName("GetConsumerSystemBios")
            .Produces<ContractConsumerPlatformBios>()
            .ProducesProblems(StatusCodes.Status400BadRequest, StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status401Unauthorized);

        return app;
    }

    private static async Task<IResult> DownloadCdAsync(int releaseId, ICurrentUser user, IConsumerCdArchiveRepository repository,
        IRomCdArchiveDownloadReader reader, HttpContext context, bool cue, CancellationToken ct)
    {
        if (user.UserId is not { } userId || userId == Guid.Empty)
            return new[] { ConsumerErrors.CurrentUserRequired() }.ToProblem();
        var authorized = await repository.FindAsync(new(userId), releaseId, ct);
        return authorized is ConsumerLibraryReadResult<RomCdArchiveDownload>.Found found
            ? await CdArchiveDownloadResults.CreateAsync(found.Value, reader, context, cue, ct)
            : new[] { ConsumerErrors.ContentNotFound() }.ToProblem();
    }

    private static async Task<IResult> GetPlatformBios(
        SystemKeyParameter systemKey,
        IQueryHandler<GetConsumerPlatformBiosQuery, ContractConsumerPlatformBios> handler,
        CancellationToken ct)
    {
        var query = new GetConsumerPlatformBiosQuery(systemKey.SystemId);
        var result = await handler.HandleAsync(query, ct);

        return result.Match(
            ok => Results.Ok(ok),
            errors => errors.ToProblem());
    }

    private static async Task<IResult> IssueReleaseManifest(
        Sqid releaseId,
        ICommandHandler<IssueConsumerReleaseManifestCommand, ContractConsumerReleaseManifest> handler,
        CancellationToken ct)
    {
        var command = new IssueConsumerReleaseManifestCommand(releaseId);
        var result = await handler.HandleAsync(command, ct);

        return result.Match(
            ok => Results.Ok(ok),
            errors => errors.ToProblem());
    }
}
