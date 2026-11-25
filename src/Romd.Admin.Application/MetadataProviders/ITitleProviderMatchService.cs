using ErrorOr;
using Romd.Admin.Application.Ingestion.Jobs;
using Romd.Application.Common.Concurrency;

namespace Romd.Admin.Application.MetadataProviders;

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
