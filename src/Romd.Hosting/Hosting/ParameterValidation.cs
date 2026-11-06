using System.ComponentModel.DataAnnotations;
using System.Reflection;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Mvc;
using Romd.Application.Common.ReferenceCatalog;
using Romd.Host.Endpoints;

namespace Romd.Hosting;

/// <summary>
///     The declared-parameter validation stage (docs/decisions/api-contract-policy.md,
///     "Strict Validation", "Pagination" and "One Error Envelope"). It runs after binding and
///     before the operation, and checks two declarations together:
///     <list type="bullet">
///         <item>
///             the <see cref="ValidationAttribute" />s on route, query and header parameters, such as
///             <c>[Range(1, MaxLimit)] int limit</c>, which OpenAPI publishes (a range as
///             <c>minimum</c> and <c>maximum</c>);
///         </item>
///         <item>the <see cref="SystemKeyParameter" />s, each resolved to its system;</item>
///         <item>
///             the enum route and query parameters, whose value must be a member name exactly: binding already
///             refuses an unknown name, but it also accepts a number, which the contract never does.
///         </item>
///     </list>
///     Every 400 failure of the stage is reported in one <c>Request.InvalidParameter</c> envelope
///     keyed by wire name. Query-name validation precedes binding and this stage. Declared
///     constraints and invalid query values are reported before resource resolution, so an unknown
///     route key answers 404 <c>Systems.NotFound</c> only when the request has no such failure.
///     ASP.NET Core's <c>AddValidation</c> is not used because it also validates every
///     annotated request body, keyed by CLR member names and under one generic code.
/// </summary>
internal static class ParameterValidation
{
    public static EndpointFilterDelegate CreateFilter(EndpointFilterFactoryContext context, EndpointFilterDelegate next)
    {
        var constrained = ConstrainedParameters(context.MethodInfo);
        var enums = EnumParameters(context.MethodInfo);
        bool resolvesSystemKeys = context.MethodInfo.GetParameters()
            .Any(parameter => parameter.ParameterType == typeof(SystemKeyParameter));
        if (constrained.Length == 0 && enums.Length == 0 && !resolvesSystemKeys)
            return next;

        return async invocation => await ValidateAsync(invocation, constrained, enums) is { } problem
            ? problem
            : await next(invocation);
    }

    /// <summary>
    ///     Runs after every endpoint convention: marks the endpoint as validated, so a test can prove
    ///     every system key has the stage; records the query parameters the handler binds, which
    ///     <see cref="RomdHttpContractExtensions.UseRomdQueryNameValidation" /> holds every request
    ///     to; and documents the 400, since any operation refuses an undeclared query name or repeated scalar.
    /// </summary>
    public static void DeclareStage(EndpointBuilder builder)
    {
        builder.Metadata.Add(ValidatedParametersMetadata.Instance);
        if (builder is RouteEndpointBuilder route && builder.Metadata.OfType<MethodInfo>().FirstOrDefault() is { } method)
        {
            builder.Metadata.Add(QueryParameterDeclaration.Of(method, route.RoutePattern,
                builder.Metadata.OfType<IParameterBindingMetadata>()));
        }

        if (!builder.Metadata.OfType<IProducesResponseTypeMetadata>().Any(response =>
                response.StatusCode == StatusCodes.Status400BadRequest && response.Type == typeof(ProblemDetails)
                && response.ContentTypes.Contains("application/problem+json")))
            builder.Metadata.Add(new ProducesResponseTypeMetadata(
                StatusCodes.Status400BadRequest,
                typeof(ProblemDetails),
                ["application/problem+json"]));
    }

    private static async ValueTask<IResult?> ValidateAsync(
        EndpointFilterInvocationContext invocation,
        (int Position, string Name, ValidationAttribute[] Attributes)[] constrained,
        (string Name, Type EnumType)[] enums)
    {
        var failures = constrained
            .Select(parameter => (parameter.Name, Messages: parameter.Attributes
                .Where(attribute => !attribute.IsValid(invocation.Arguments[parameter.Position]))
                .Select(attribute => attribute.FormatErrorMessage(parameter.Name))
                .ToArray()))
            .Where(parameter => parameter.Messages.Length > 0)
            .ToDictionary(parameter => parameter.Name, parameter => parameter.Messages);
        foreach (var (name, enumType) in enums)
            if (RawValue(invocation.HttpContext, name) is { Length: > 0 } raw &&
                !Enum.GetNames(enumType).Contains(raw, StringComparer.Ordinal))
                failures[name] = [InvalidEnumMessage];

        bool unknownRouteKey = false;
        var bindings = invocation.HttpContext.GetEndpoint()?.Metadata.GetOrderedMetadata<SystemKeyBinding>() ?? [];
        foreach (var binding in bindings)
        {
            if (invocation.Arguments[binding.Position] is not SystemKeyParameter { IsResolved: false } key)
                continue;

            var catalog = invocation.HttpContext.RequestServices.GetRequiredService<IReferenceCatalogService>();
            if (await catalog.ResolveSystemIdAsync(key.Key, invocation.HttpContext.RequestAborted) is { } systemId)
                invocation.Arguments[binding.Position] = key.Resolve(systemId);
            else if (binding.InRoute)
                unknownRouteKey = true;
            else
                failures[binding.Name] = [SystemIdentityResolution.UnknownKeyMessage];
        }

        if (failures.Count > 0)
            return ProblemResults.ValidationProblem(ProblemCodes.RequestInvalidParameter, failures);

        return unknownRouteKey
            ? ProblemResults.Problem(StatusCodes.Status404NotFound, ProblemCodes.SystemsNotFound, "No system has this key.")
            : null;
    }

    private const string InvalidEnumMessage = "The value is not valid.";

    /// <summary>The enum parameters a route or query value binds, by wire name.</summary>
    private static (string Name, Type EnumType)[] EnumParameters(MethodInfo method) =>
        method.GetParameters()
            .Select(parameter => (Parameter: parameter,
                EnumType: Nullable.GetUnderlyingType(parameter.ParameterType) ?? parameter.ParameterType))
            .Where(parameter => parameter.EnumType.IsEnum &&
                !parameter.Parameter.GetCustomAttributes(inherit: true).Any(attribute => attribute is IFromBodyMetadata or IFromServiceMetadata))
            .Select(parameter => (ParameterWireName.Of(parameter.Parameter), parameter.EnumType))
            .ToArray();

    private static string? RawValue(HttpContext context, string name) =>
        context.Request.Query.TryGetValue(name, out var query) ? query.ToString() :
        context.Request.RouteValues.TryGetValue(name, out var route) ? route?.ToString() : null;

    private static (int Position, string Name, ValidationAttribute[] Attributes)[] ConstrainedParameters(MethodInfo method) =>
        method.GetParameters()
            .Select(parameter => (parameter.Position, Name: ParameterWireName.Of(parameter),
                Attributes: parameter.GetCustomAttributes<ValidationAttribute>(inherit: true).ToArray()))
            .Where(parameter => parameter.Attributes.Length > 0)
            .ToArray();
}

/// <summary>Marks an endpoint whose route group runs the declared-parameter stage.</summary>
internal sealed class ValidatedParametersMetadata
{
    public static readonly ValidatedParametersMetadata Instance = new();

    private ValidatedParametersMetadata()
    {
    }
}
