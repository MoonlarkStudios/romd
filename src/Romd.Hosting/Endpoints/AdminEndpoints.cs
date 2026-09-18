using Microsoft.AspNetCore.Mvc;
using Romd.Admin.Application.Catalog.Commands.BackfillBiosCatalog;
using Romd.Admin.Application.Source.Dat;
using Romd.Application.Common.Cqrs;
using Romd.Host.Authorization;

namespace Romd.Host.Endpoints;

/// <summary>
///     Endpoint definitions for administrative operations.
/// </summary>
public static class AdminEndpoints
{
    /// <summary>
    ///     Maps all admin-related endpoints to the application.
    /// </summary>
    public static IEndpointRouteBuilder MapAdminEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/admin")
            .WithTags("Admin");

        group.MapPost("backfill-bios", BackfillBios)
            .WithName("BackfillBios")
            .WithDescription("Backfill IsBios flag for existing games based on naming patterns")
            .Produces<BackfillBiosResult>()
            .Produces<ProblemDetails>(StatusCodes.Status500InternalServerError)
            .RequireAuthorization(AuthorizationPolicies.RequireAdmin);

        group.MapPost("backfill-bios-catalog", BackfillBiosCatalog)
            .WithName("BackfillBiosCatalog")
            .WithDescription("Group existing BIOS games of platform-assigned DATs into BIOS catalog entries")
            .Produces<BackfillBiosCatalogResult>()
            .Produces<ProblemDetails>(StatusCodes.Status500InternalServerError)
            .RequireAuthorization(AuthorizationPolicies.RequireAdmin);

        return app;
    }

    private static async Task<IResult> BackfillBios(
        IDatRepository repository,
        CancellationToken cancellationToken = default)
    {
        // Unhandled exceptions surface as the sanitized 500 envelope via the shared handler.
        int updated = await repository.BackfillIsBiosAsync(cancellationToken);
        return Results.Ok(new BackfillBiosResult(updated));
    }

    private static async Task<IResult> BackfillBiosCatalog(
        ICommandHandler<BackfillBiosCatalogCommand, BackfillBiosCatalogResult> handler,
        CancellationToken cancellationToken = default)
    {
        var result = await handler.HandleAsync(new BackfillBiosCatalogCommand(), cancellationToken);
        return result.Match(Results.Ok, errors => errors.ToProblem());
    }

}

/// <summary>
///     Result of the BIOS backfill operation.
/// </summary>
public sealed record BackfillBiosResult(int GamesUpdated);
