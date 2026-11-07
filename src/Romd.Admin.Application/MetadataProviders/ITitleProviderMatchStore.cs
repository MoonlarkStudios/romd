using System.Security.Cryptography;
using System.Text;
using ErrorOr;
using Romd.Application.Common.Concurrency;
using Romd.Contracts.Management.MetadataProviders;
using Romd.Domain.Catalog;
using Romd.Domain.Jobs;

namespace Romd.Admin.Application.MetadataProviders;

public sealed record ProviderMatchState(string ProviderId, Guid Revision, bool SuppressAutomaticMatch,
    bool IdentityChanged, string? Evidence, string? MatcherVersion, ProviderGame? Game,
    ProviderIdentityDecision? Decision);
public sealed record TitleProviderMatchFacts(IReadOnlyList<TitleExternalId> Links,
    IReadOnlyList<ProviderMatchState> States, IReadOnlyList<TitleMetadataLayer> Layers);
public sealed record ProviderMatchTitle(int Id, string Name, int PlatformId);

/// <summary>Detached reads and staged match writes. Every staged operation requires the caller's transaction.</summary>
public interface ITitleProviderMatchStore
{
    Task<bool> ExistsAsync(int titleId, CancellationToken ct);
    Task<bool> IsSuppressedAsync(int titleId, string providerId, CancellationToken ct);
    Task<Guid?> GetLinkRevisionAsync(int titleId, string providerId, string gameId, CancellationToken ct);
    Task<Guid> ReadRevisionAsync(int titleId, string providerId, CancellationToken ct);
    /// <summary>Reads and parses only the requested providers, keeping unrelated persisted data isolated.</summary>
    Task<TitleProviderMatchFacts> ReadFactsAsync(int titleId, IReadOnlyCollection<string> providerIds, CancellationToken ct);
    Task<ErrorOr<Success>> StageChangeAsync(int titleId, string providerId, RevisionPrecondition ifMatch,
        ProviderGame? game, bool unlink, CancellationToken ct);
    Task<Guid?> FindPendingEnrichmentAsync(int titleId, CancellationToken ct);
    Task<ProviderMatchTitle> ReadTitleAsync(int titleId, CancellationToken ct);
    Task AddEnrichmentStagedAsync(EnrichmentJob job, CancellationToken ct);
}

public static class ProviderMatchRevisions
{
    public static Guid For(Guid? stateRevision, string? externalId, bool? confirmed) =>
        new(SHA256.HashData(Encoding.UTF8.GetBytes($"{stateRevision}:{externalId}:{confirmed}"))[..16]);
}
