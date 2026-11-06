using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Mvc;
using Romd.Hosting;

namespace Romd.Host.Endpoints;

/// <summary>
///     A <c>systemKey</c> route or query parameter, resolved to the internal system identity
///     (docs/decisions/api-contract-policy.md, "Opaque Public Identity" and "Strict Validation").
///     Binding the type publishes the failure it can answer and records the binding for the
///     declared-parameter stage of the /api group (<see cref="RomdHttpContractExtensions.WithRomdParameterValidation" />),
///     which resolves it with the other declared constraints: an unknown key in the route is a 404
///     <c>Systems.NotFound</c>, and an unknown key in the query is a 400
///     <c>Request.InvalidParameter</c> keyed by the parameter's wire name. The type binds only as a
///     handler parameter, never inside <c>[AsParameters]</c>. OpenAPI keeps the parameter a string.
/// </summary>
public sealed class SystemKeyParameter : IParsable<SystemKeyParameter>, IEndpointParameterMetadataProvider
{
    private readonly int? _systemId;

    private SystemKeyParameter(string key, int? systemId)
    {
        Key = key;
        _systemId = systemId;
    }

    /// <summary>The public natural key as the client sent it.</summary>
    public string Key { get; }

    /// <summary>The internal system id, resolved by the declared-parameter stage before the handler runs.</summary>
    public int SystemId =>
        _systemId ?? throw new InvalidOperationException("The system key was bound outside a parameter-validated /api group.");

    internal bool IsResolved => _systemId is not null;

    internal SystemKeyParameter Resolve(int systemId) => new(Key, systemId);

    public static SystemKeyParameter Parse(string s, IFormatProvider? provider) =>
        TryParse(s, provider, out var result) ? result : throw new FormatException("The value is not a system key.");

    public static bool TryParse(
        [NotNullWhen(true)] string? s,
        IFormatProvider? provider,
        [MaybeNullWhen(false)] out SystemKeyParameter result)
    {
        result = string.IsNullOrWhiteSpace(s) ? null : new SystemKeyParameter(s, null);
        return result is not null;
    }

    public static void PopulateMetadata(ParameterInfo parameter, EndpointBuilder builder)
    {
        // An [AsParameters] property has no handler argument position for the stage to resolve.
        if (parameter.Position < 0 || parameter.Member is not MethodBase)
            throw new NotSupportedException("A system key binds only as a handler parameter, not inside [AsParameters].");

        string name = ParameterWireName.Of(parameter);
        bool inRoute = builder is RouteEndpointBuilder route && route.RoutePattern.GetParameter(name) is not null;
        builder.Metadata.Add(new SystemKeyBinding(parameter.Position, name, inRoute));
        builder.Metadata.Add(new ProducesResponseTypeMetadata(
            inRoute ? StatusCodes.Status404NotFound : StatusCodes.Status400BadRequest,
            typeof(ProblemDetails),
            ["application/problem+json"]));
    }
}

/// <summary>Where a <see cref="SystemKeyParameter" /> binds: its handler argument, wire name and source.</summary>
internal sealed record SystemKeyBinding(int Position, string Name, bool InRoute);
