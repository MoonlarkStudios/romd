using System.Reflection;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Primitives;

namespace Romd.Hosting;

/// <summary>
///     The query parameters an /api operation accepts, by exact wire name, and whether each may repeat
///     (docs/decisions/api-contract-policy.md, "Strict Validation"). It is derived from the handler's parameters with
///     minimal APIs' published binding metadata, and tests hold it equal to ApiExplorer and to both published OpenAPI
///     documents. A binding it cannot classify, <c>[AsParameters]</c> or a custom <c>BindAsync</c>, is refused when the
///     endpoint is built.
/// </summary>
internal sealed class QueryParameterDeclaration
{
    private const string UnsupportedMessage = "This query parameter is not supported.";
    private const string RepeatedMessage = "Supply exactly one value for this query parameter.";

    private static readonly IReadOnlyDictionary<string, string[]> NoRefusals = new Dictionary<string, string[]>();

    private QueryParameterDeclaration(IReadOnlyDictionary<string, bool> parameters) => Parameters = parameters;

    /// <summary>Each declared name, and whether it may be supplied more than once.</summary>
    public IReadOnlyDictionary<string, bool> Parameters { get; }

    public static QueryParameterDeclaration Of(MethodInfo handler, RoutePattern route,
        IEnumerable<IParameterBindingMetadata> bindings)
    {
        var metadata = bindings.ToArray();
        var parameters = new Dictionary<string, bool>(StringComparer.Ordinal);
        foreach (var parameter in handler.GetParameters())
        {
            if (parameter.IsDefined(typeof(AsParametersAttribute), inherit: true))
                throw Unsupported(parameter, "[AsParameters] hides its members' query bindings");
            var binding = metadata.LastOrDefault(candidate => candidate.ParameterInfo.Member == parameter.Member
                && candidate.ParameterInfo.Position == parameter.Position)
                ?? throw Unsupported(parameter, "the framework did not publish binding metadata");
            if (QueryBinding(binding, route) is { } repeatable)
                parameters[ParameterWireName.Of(parameter)] = repeatable;
        }
        return new QueryParameterDeclaration(parameters);
    }

    /// <summary>
    ///     The supplied names the operation does not declare, and the single-valued ones supplied more than once, each
    ///     with its message. Every occurrence is decoded once and compared ordinally: the request's query collection
    ///     merges names case-insensitively, so it cannot show an occurrence's own spelling.
    /// </summary>
    public IReadOnlyDictionary<string, string[]> Refusals(QueryString query)
    {
        if (!query.HasValue)
            return NoRefusals;

        Dictionary<string, string[]>? refusals = null;
        HashSet<string>? supplied = null;
        foreach (var pair in new QueryStringEnumerable(query.Value))
        {
            string name = pair.DecodeName().ToString();
            if (!Parameters.TryGetValue(name, out bool repeatable))
                (refusals ??= new Dictionary<string, string[]>(StringComparer.Ordinal))[name] = [UnsupportedMessage];
            else if (!repeatable && !(supplied ??= new HashSet<string>(StringComparer.Ordinal)).Add(name))
                (refusals ??= new Dictionary<string, string[]>(StringComparer.Ordinal))[name] = [RepeatedMessage];
        }

        return refusals ?? NoRefusals;
    }

    // Explicit sources take precedence. For inferred bindings, use the framework's flags rather than duplicating its
    // parser/BindAsync discovery and body inference (which include Uri.TryCreate and inherited static methods).
    // Null means the parameter does not bind from the query; otherwise the value says whether it repeats.
    private static bool? QueryBinding(IParameterBindingMetadata binding, RoutePattern route)
    {
        var parameter = binding.ParameterInfo;
        var attributes = parameter.GetCustomAttributes(inherit: true);
        var type = Nullable.GetUnderlyingType(parameter.ParameterType) ?? parameter.ParameterType;
        if (attributes.OfType<IFromRouteMetadata>().Any())
            return null;
        if (attributes.OfType<IFromQueryMetadata>().Any())
            return type.IsArray || type == typeof(StringValues);
        if (attributes.Any(attribute => attribute is IFromHeaderMetadata or IFromBodyMetadata
                or IFromFormMetadata or IFromServiceMetadata or FromKeyedServicesAttribute))
            return null;
        if (binding.HasBindAsync)
            throw Unsupported(parameter, "a custom BindAsync binding cannot declare its query names");
        if (!binding.HasTryParse)
            return null;
        if (type.IsArray || type == typeof(StringValues))
            return true;
        return route.GetParameter(binding.Name) is null ? false : null;
    }

    private static NotSupportedException Unsupported(ParameterInfo parameter, string reason) =>
        new($"{parameter.Member.Name}({parameter.Name}): {reason}. Bind query parameters as handler parameters.");
}
