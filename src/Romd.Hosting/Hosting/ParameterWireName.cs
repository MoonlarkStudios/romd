using System.Reflection;
using Microsoft.AspNetCore.Http.Metadata;

namespace Romd.Hosting;

/// <summary>The name a handler parameter binds under; a <c>[From*(Name = …)]</c> renames it on the wire.</summary>
internal static class ParameterWireName
{
    public static string Of(ParameterInfo parameter) =>
        parameter.GetCustomAttributes(inherit: true)
            .Select(attribute => attribute switch
            {
                IFromQueryMetadata query => query.Name,
                IFromHeaderMetadata header => header.Name,
                IFromRouteMetadata route => route.Name,
                _ => null
            })
            .FirstOrDefault(name => !string.IsNullOrEmpty(name))
        ?? parameter.Name!;
}
