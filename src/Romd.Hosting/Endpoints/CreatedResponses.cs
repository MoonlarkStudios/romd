using Microsoft.AspNetCore.OpenApi;
using Microsoft.Net.Http.Headers;
using Microsoft.OpenApi;

namespace Romd.Host.Endpoints;

/// <summary>
///     The one declaration of a create's 201: the created member's representation and the <c>Location</c> header
///     that addresses it (docs/decisions/api-contract-policy.md, "Method And Status Semantics").
/// </summary>
public static class CreatedResponses
{
    public static RouteHandlerBuilder ProducesCreated<T>(this RouteHandlerBuilder builder) =>
        builder.Produces<T>(StatusCodes.Status201Created).AddOpenApiOperationTransformer((operation, _, _) =>
        {
            if (operation.Responses?.GetValueOrDefault(StatusCodes.Status201Created.ToString()) is OpenApiResponse created)
            {
                created.Headers ??= new Dictionary<string, IOpenApiHeader>(StringComparer.Ordinal);
                created.Headers[HeaderNames.Location] = new OpenApiHeader
                {
                    Description = "The created member's address.",
                    Required = true,
                    Schema = new OpenApiSchema { Type = JsonSchemaType.String }
                };
            }

            return Task.CompletedTask;
        });
}
