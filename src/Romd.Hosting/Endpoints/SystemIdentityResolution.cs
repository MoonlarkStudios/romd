using Romd.Application.Common.ReferenceCatalog;

namespace Romd.Host.Endpoints;

/// <summary>
///     Resolves the system keys a request body carries. Route and query keys bind as
///     <see cref="SystemKeyParameter" /> instead. An unknown body key is a 400 keyed by its field
///     (docs/decisions/api-contract-policy.md, "Strict Validation"); messages never echo the key.
/// </summary>
internal static class SystemIdentityResolution
{
    /// <summary>The one message for an unknown system key, wherever it is supplied; it never echoes the key.</summary>
    internal const string UnknownKeyMessage = "Not a known system key.";

    /// <summary>A missing or unknown key is a 400 <c>Systems.UnknownKey</c>.</summary>
    internal static async Task<(IResult? Problem, int SystemId)> ResolveBodySystemKeyAsync(
        this IReferenceCatalogService service, string? key, string field, CancellationToken ct) =>
        !string.IsNullOrWhiteSpace(key) && await service.ResolveSystemIdAsync(key, ct) is { } systemId
            ? (null, systemId)
            : (ProblemResults.ValidationProblem(ProblemCodes.SystemsUnknownKey, field, UnknownKeyMessage), 0);

    /// <summary>An absent key is valid and resolves to <c>null</c>.</summary>
    internal static async Task<(IResult? Problem, int? SystemId)> ResolveOptionalBodySystemKeyAsync(
        this IReferenceCatalogService service, string? key, string field, CancellationToken ct)
    {
        if (key is null)
            return (null, null);

        var (problem, systemId) = await service.ResolveBodySystemKeyAsync(key, field, ct);
        return (problem, problem is null ? systemId : null);
    }

    /// <summary>
    ///     Resolves every key in order and records each unknown entry, by its index, under
    ///     <paramref name="field" /> in <paramref name="failures" />, so a caller can report every
    ///     failure of a body in one envelope.
    /// </summary>
    internal static async Task<IReadOnlyList<int>> CollectBodySystemKeysAsync(
        this IReferenceCatalogService service,
        IReadOnlyList<string?> keys,
        string field,
        IDictionary<string, string[]> failures,
        CancellationToken ct)
    {
        var systemIds = new List<int>(keys.Count);
        var unknown = new List<string>();
        for (int index = 0; index < keys.Count; index++)
        {
            if (!string.IsNullOrWhiteSpace(keys[index]) && await service.ResolveSystemIdAsync(keys[index]!, ct) is { } systemId)
                systemIds.Add(systemId);
            else
                unknown.Add($"Entry {index} is not a known system key.");
        }

        if (unknown.Count > 0)
            failures[field] = [.. unknown];
        return systemIds;
    }
}
