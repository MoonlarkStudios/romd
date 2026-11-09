using ErrorOr;
using Romd.Domain.Catalog;
using Romd.Contracts.Management.MetadataProviders;
using Romd.Admin.Application.Ingestion.Jobs;
using Romd.Application.Common.Concurrency;

namespace Romd.Admin.Application.MetadataProviders;

public sealed record ProviderAvailability(bool Enabled, bool Configured);

public interface IProviderIdentityAdapter
{
    string Id { get; }
    string Name { get; }
    ProviderIdentityScope IdentityScope => ProviderIdentityScope.Release;
    IReadOnlyList<string> Capabilities { get; }
    Task<ProviderAvailability> GetAvailabilityAsync(CancellationToken ct);
    Task<ErrorOr<ProviderGameSearchResult>> SearchAsync(string query, CancellationToken ct);
    Task<ErrorOr<ProviderGame>> ResolveAsync(string idOrUrl, CancellationToken ct);
}

public interface ITitleProviderMatchService
{
    Task<ProviderIdentityDiscoveryResult> DiscoverAsync(int titleId, JobContext context, CancellationToken ct);
    Task<bool> IsSuppressedAsync(int titleId, string providerId, CancellationToken ct);
    Task<Guid?> GetLinkRevisionAsync(int titleId, string providerId, string gameId, CancellationToken ct);
    Task<ErrorOr<IReadOnlyList<TitleProviderMatch>>> GetAsync(int titleId, CancellationToken ct);

    /// <summary>
    ///     One enabled provider's match for the title. An unknown provider is not found; a disabled one is a conflict
    ///     with local configuration.
    /// </summary>
    Task<ErrorOr<TitleProviderMatch>> GetAsync(int titleId, string providerId, CancellationToken ct);

    /// <summary>
    ///     Bounded live provider suggestions, with explicit completeness rather than a catalog enumeration promise.
    /// </summary>
    Task<ErrorOr<ProviderGameSearchResult>> SearchAsync(string providerId, string query, CancellationToken ct);

    /// <summary>Resolves one game by id or page URL independently of limited free-text suggestions.</summary>
    Task<ErrorOr<ProviderGame>> ResolveAsync(string providerId, string input, CancellationToken ct);

    /// <summary>
    ///     Writes under <paramref name="ifMatch" />, compared with the match's derived revision while the title row is
    ///     locked. Each write rotates the title revision and answers the match as written; set and confirm also name
    ///     the job queued or joined to apply it as <see cref="TitleProviderMatch.PendingJobId" />. An unknown
    ///     provider is not found, and a disabled or unconfigured one a conflict.
    /// </summary>
    Task<ErrorOr<TitleProviderMatch>> SetAsync(int titleId, string providerId, string externalId,
        RevisionPrecondition ifMatch, CancellationToken ct);

    Task<ErrorOr<TitleProviderMatch>> ConfirmAsync(int titleId, string providerId, RevisionPrecondition ifMatch,
        CancellationToken ct);

    Task<ErrorOr<TitleProviderMatch>> UnlinkAsync(int titleId, string providerId, RevisionPrecondition ifMatch,
        CancellationToken ct);
}

public static class ProviderMatchErrors
{
    public static Error InvalidInput => Error.Validation("ProviderMatch.InvalidInput", "Enter a valid provider game ID or page URL.");
    public static Error InvalidQuery => Error.Validation("ProviderMatch.InvalidQuery", "Enter a search query between 1 and 200 characters without control characters.");
    public static Error InvalidResponse => Error.Custom(502, "ProviderMatch.InvalidResponse", "The provider returned an invalid response.");
    public static Error Unavailable => Error.Custom(503, "ProviderMatch.Unavailable", "The provider is unavailable. Try again later.");
    /// <summary>A provider switched off or without credentials: local configuration, not an upstream failure.</summary>
    public static Error NotConfigured => Error.Conflict("ProviderMatch.ProviderNotConfigured",
        "Enable and configure this provider before using it.");
    public static Error UnknownProvider => Error.NotFound("ProviderMatch.UnknownProvider", "No metadata provider has this id.");
    public static Error NotFound => Error.NotFound("ProviderMatch.NotFound", "The provider game was not found.");
    /// <summary>An id, page URL or query the provider resolves to no game: the caller's input, not a missing resource.</summary>
    public static Error Unresolvable => Error.Validation("ProviderMatch.Unresolvable",
        "The provider has no game with this id or page URL.");
    public static Error NotLinked => Error.Conflict("ProviderMatch.NotLinked", "The title has no match on this provider to confirm.");
    public static Error Conflict => Error.Conflict("ProviderMatch.Conflict", "The provider match changed. Reload before trying again.");
    public static Error PreconditionFailed => Error.Custom(412, "ProviderMatch.PreconditionFailed",
        "The provider match changed since it was read. Reload it, then decide again.");
}
