namespace Romd.Contracts.Management.Titles;

public sealed record TitleLookupNames(IReadOnlyList<string> Names, string Revision);

public sealed record ReplaceTitleLookupNamesRequest
{
    /// <summary>Up to 32 alternate names, each at most 200 characters. An empty list clears the names.</summary>
    public required IReadOnlyList<string> Names { get; init; }
}
