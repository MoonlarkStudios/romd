using System.Collections.Immutable;

namespace Romd.Application.Common.Security;

public static class ConsumerGrantScopes
{
    public const string Discovery = "romd:discovery";
    public const string Library = "romd:library";
    public const string Releases = "romd:releases";
    public const string Delivery = "romd:delivery";

    public static ImmutableArray<string> All { get; } = [Discovery, Library, Releases, Delivery];
}
