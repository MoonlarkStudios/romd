using System.Diagnostics;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Diagnostics;
using Romd.Host.Endpoints;
using Romd.Hosting.Serialization;

namespace Romd.Hosting;

/// <summary>
///     The HTTP contract configuration both API hosts register
///     (docs/decisions/api-contract-policy.md, "Canonical Hosts And Contract Documents"): strict
///     number handling, named-string enums, enveloped binding failures, the ProblemDetails
///     customization, the sanitized exception handler, the /api routing problems and the
///     parameter validation each surface applies to its /api group. Surface-specific exception
///     handling layers on top as <see cref="IExceptionHandler" /> registrations in that surface only.
/// </summary>
public static class RomdHttpContractExtensions
{
    public static IServiceCollection AddRomdHttpContract(this IServiceCollection services)
    {
        // Contract enums serialize as named strings, never integers
        // (docs/decisions/api-contract-policy.md, "Opaque Public Identity"). Reading is strict
        // too: only exact ordinal declared names bind; integers, case variants, comma-composed
        // values and unknown names fail. Quoted numerics fail as well
        // (docs/decisions/api-contract-policy.md, "Type-Faithful Numeric Wire Contracts").
        services.ConfigureHttpJsonOptions(options =>
        {
            options.SerializerOptions.NumberHandling = JsonNumberHandling.Strict;
            options.SerializerOptions.Converters.Add(new StrictJsonStringEnumConverter());
        });

        // Binding failures (malformed JSON bodies, invalid enum names, unparsable parameters)
        // must yield a deterministic 400 envelope in every environment, not the framework's
        // bodyless 400 outside Development. Throwing routes them through the exception handler,
        // which preserves the BadHttpRequestException status code.
        services.Configure<RouteHandlerOptions>(options => options.ThrowOnBadRequest = true);

        // Every problem body is a sanitized envelope
        // (docs/decisions/api-contract-policy.md, "One Error Envelope"): generic title, no
        // exception message or stack, plus the errorCode/traceId extensions. Endpoint-produced
        // problems already carry errorCode, so TryAdd only fills problems written without one.
        // A binding failure names the failed parameter (docs/decisions/api-contract-policy.md,
        // "Strict Validation").
        services.AddProblemDetails(options => options.CustomizeProblemDetails = context =>
        {
            var handled = context.HttpContext.Features.Get<IExceptionHandlerFeature>();
            if (handled?.Error is BadHttpRequestException badRequest)
            {
                var (errorCode, errors) = BindingFailureProblem.Describe(badRequest, handled.Endpoint);
                context.ProblemDetails.Extensions.TryAdd(ProblemResults.ErrorCodeExtensionName, errorCode);
                if (errors is not null)
                    context.ProblemDetails.Extensions.TryAdd(ProblemResults.ErrorsExtensionName, errors);
            }

            context.ProblemDetails.Extensions.TryAdd(
                ProblemResults.ErrorCodeExtensionName,
                handled?.Error is null ? ProblemCodes.GeneralUnexpected : ProblemCodes.GeneralUnhandled);
            context.ProblemDetails.Extensions.TryAdd(
                ProblemResults.TraceIdExtensionName,
                Activity.Current?.Id ?? context.HttpContext.TraceIdentifier);
        });

        return services;
    }

    /// <summary>
    ///     Runs the declared-parameter stage (<see cref="ParameterValidation" />) on the group: the
    ///     validation attributes on route, query and header parameters, such as a <c>limit</c>
    ///     range, and <see cref="SystemKeyParameter" /> resolution. Each surface applies it to its
    ///     /api group.
    /// </summary>
    public static RouteGroupBuilder WithRomdParameterValidation(this RouteGroupBuilder api)
    {
        api.AddEndpointFilterFactory(ParameterValidation.CreateFilter);
        ((IEndpointConventionBuilder)api).Finally(ParameterValidation.DeclareStage);
        return api;
    }

    /// <summary>
    ///     Gives /api routing outcomes the shared envelope: an unmatched route is a 404, and the
    ///     method and media-type rejections routing selects stay 405 and 415. No fallback may match
    ///     /api: a catch-all <c>MapFallback</c> stays a candidate after routing rejects the real
    ///     endpoint and so turns 405 and 415 into a fallback response. A request body over the operation's size limit is rejected while
    ///     binding and leaves the response unwritten, so its 413 gets the envelope here too. Place
    ///     it after authorization, so 401 and 403 are untouched.
    /// </summary>
    public static IApplicationBuilder UseRomdApiRoutingProblems(this IApplicationBuilder app) =>
        app.Use(async (context, next) =>
        {
            if (!context.Request.Path.StartsWithSegments("/api"))
            {
                await next(context);
                return;
            }

            var endpoint = context.GetEndpoint();
            if (endpoint is null)
            {
                await ProblemResults.Problem(
                        StatusCodes.Status404NotFound,
                        ProblemCodes.RequestRouteNotFound,
                        "No API operation matches this route.")
                    .ExecuteAsync(context);
                return;
            }

            await next(context);

            if (context.Response.HasStarted)
                return;

            // Routing's rejection endpoints are not route endpoints and write no body.
            string? errorCode = (context.Response.StatusCode, endpoint) switch
            {
                (StatusCodes.Status405MethodNotAllowed, not RouteEndpoint) => ProblemCodes.RequestMethodNotAllowed,
                (StatusCodes.Status415UnsupportedMediaType, not RouteEndpoint) => ProblemCodes.RequestUnsupportedMediaType,
                (StatusCodes.Status413PayloadTooLarge, _) => ProblemCodes.RequestPayloadTooLarge,
                _ => null
            };
            if (errorCode is not null)
                await ProblemResults.Problem(context.Response.StatusCode, errorCode).ExecuteAsync(context);
        });

    /// <summary>
    ///     Refuses an /api request whose query names a parameter its operation does not declare, or
    ///     supplies a single-valued one more than once (docs/decisions/api-contract-policy.md,
    ///     "Strict Validation"), keyed by the name as sent. It runs after authorization and the routing
    ///     problems, and before the endpoint binds a parameter or reads the body, so these failures
    ///     are reported alone. Routing's rejection endpoints are not route endpoints and pass through.
    ///     A route endpoint without a declaration is a configuration error: it throws, and the
    ///     exception handler answers a sanitized 500.
    /// </summary>
    public static IApplicationBuilder UseRomdQueryNameValidation(this IApplicationBuilder app) =>
        app.Use(async (context, next) =>
        {
            if (context.Request.Path.StartsWithSegments("/api") && context.GetEndpoint() is RouteEndpoint endpoint)
            {
                var declaration = endpoint.Metadata.GetMetadata<QueryParameterDeclaration>()
                    ?? throw new InvalidOperationException(
                        $"The /api endpoint '{endpoint.DisplayName}' has no query declaration; map it in a WithRomdParameterValidation group.");
                var refusals = declaration.Refusals(context.Request.QueryString);
                if (refusals.Count > 0)
                {
                    await ProblemResults.ValidationProblem(ProblemCodes.RequestInvalidParameter, refusals).ExecuteAsync(context);
                    return;
                }
            }

            await next(context);
        });

    /// <summary>
    ///     Converts unhandled exceptions into the sanitized envelopes written by the ProblemDetails
    ///     service registered in <see cref="AddRomdHttpContract" />. Registered
    ///     <see cref="IExceptionHandler" /> services run first.
    ///     <see cref="BadHttpRequestException" /> keeps its own status code and is a client error,
    ///     so it emits no error diagnostics; every other exception returns 500.
    /// </summary>
    public static IApplicationBuilder UseRomdExceptionHandling(this IApplicationBuilder app) =>
        app.UseExceptionHandler(new ExceptionHandlerOptions
        {
            StatusCodeSelector = static exception => exception is BadHttpRequestException badRequest
                ? badRequest.StatusCode
                : StatusCodes.Status500InternalServerError,
            // Keeps the framework default (an IExceptionHandler that handled the exception owns
            // its diagnostics) and adds binding failures.
            SuppressDiagnosticsCallback = static context =>
                context.Exception is BadHttpRequestException
                || context.ExceptionHandledBy == ExceptionHandledType.ExceptionHandlerService
        });
}
