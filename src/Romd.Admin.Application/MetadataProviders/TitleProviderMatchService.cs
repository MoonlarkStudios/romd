using ErrorOr;
using Microsoft.Extensions.Logging;
using Romd.Admin.Application.Artwork;
using Romd.Admin.Application.Catalog;
using Romd.Admin.Application.Common.Persistence;
using Romd.Admin.Application.Ingestion.Jobs;
using Romd.Application.Common.Concurrency;
using Romd.Application.Common.Validation;
using Romd.Contracts.Management.MetadataProviders;
using Romd.Domain.Jobs;

namespace Romd.Admin.Application.MetadataProviders;

/// <summary>Provider matching use cases. Remote I/O stays outside short caller-owned database transactions.</summary>
public sealed class TitleProviderMatchService : ITitleProviderMatchService
{
    private readonly ITitleProviderMatchStore _store;
    private readonly IEnumerable<IProviderIdentityAdapter> _adapters;
    private readonly IUnitOfWork _unitOfWork;
    private readonly TimeProvider _clock;
    private readonly ILogger<TitleProviderMatchService> _logger;
    private readonly IProviderIdentityService? _identities;
    private readonly IProviderIdentityStore? _identityStore;
    private readonly IArtworkCompletionStore? _completion;

    public TitleProviderMatchService(ITitleProviderMatchStore store,
        IEnumerable<IProviderIdentityAdapter> adapters, IUnitOfWork unitOfWork, TimeProvider clock,
        ILogger<TitleProviderMatchService> logger, IProviderIdentityService? identities = null,
        IProviderIdentityStore? identityStore = null, IArtworkCompletionStore? completion = null)
    {
        _store = store;
        _adapters = adapters;
        _unitOfWork = unitOfWork;
        _clock = clock;
        _logger = logger;
        _identities = identities;
        _identityStore = identityStore;
        _completion = completion;
    }

    private IProviderIdentityAdapter? Adapter(string id) => _adapters.SingleOrDefault(x => x.Id == id);

    public Task<bool> IsSuppressedAsync(int titleId, string providerId, CancellationToken ct) =>
        _store.IsSuppressedAsync(titleId, providerId, ct);

    public Task<Guid?> GetLinkRevisionAsync(int titleId, string providerId, string gameId, CancellationToken ct) =>
        _store.GetLinkRevisionAsync(titleId, providerId, gameId, ct);

    public Task<ProviderIdentityDiscoveryResult> DiscoverAsync(int titleId, JobContext context, CancellationToken ct) =>
        _identities?.DiscoverAsync(titleId, context, ct) ?? Task.FromResult(ProviderIdentityDiscoveryResult.Empty);

    public async Task<ErrorOr<IReadOnlyList<TitleProviderMatch>>> GetAsync(int titleId, CancellationToken ct)
    {
        if (!await _store.ExistsAsync(titleId, ct))
            return CatalogErrors.TitleNotFound();
        var enabled = new List<(IProviderIdentityAdapter Adapter, ProviderAvailability Availability)>();
        foreach (var adapter in _adapters.OrderBy(x => x.Name))
        {
            var availability = await adapter.GetAvailabilityAsync(ct);
            if (availability.Enabled)
                enabled.Add((adapter, availability));
        }
        if (enabled.Count == 0)
            return new List<TitleProviderMatch>();

        var facts = await _store.ReadFactsAsync(titleId, enabled.Select(provider => provider.Adapter.Id).ToArray(), ct);
        var rows = new List<TitleProviderMatch>(enabled.Count);
        foreach (var (adapter, availability) in enabled)
            rows.Add(await RowAsync(adapter, availability, facts, await ReadTrustedIdentityAsync(titleId, adapter.Id, ct), null, ct));
        return rows;
    }

    public async Task<ErrorOr<TitleProviderMatch>> GetAsync(int titleId, string providerId, CancellationToken ct)
    {
        if (!await _store.ExistsAsync(titleId, ct))
            return CatalogErrors.TitleNotFound();
        if (Adapter(providerId) is not { } adapter)
            return ProviderMatchErrors.UnknownProvider;
        var availability = await adapter.GetAvailabilityAsync(ct);
        if (!availability.Enabled)
            return ProviderMatchErrors.NotConfigured;
        return await RowAsync(adapter, availability, await _store.ReadFactsAsync(titleId, [providerId], ct),
            await ReadTrustedIdentityAsync(titleId, providerId, ct), null, ct);
    }

    public async Task<ErrorOr<ProviderGameSearchResult>> SearchAsync(string providerId, string query, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(query) || query.Length > 200 || query.Any(char.IsControl))
            return InputErrors([ProviderMatchErrors.InvalidQuery], "query");
        var provider = await UsableAsync(providerId, ct);
        if (provider.IsError)
            return provider.Errors;
        var games = await provider.Value.Adapter.SearchAsync(query.Trim(), ct);
        return games.IsError ? InputErrors(games.Errors, "query") : games;
    }

    public async Task<ErrorOr<ProviderGame>> ResolveAsync(string providerId, string input, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(input) || input.Length > 500 || input.Any(char.IsControl))
            return InputErrors([ProviderMatchErrors.InvalidInput], "input");
        var provider = await UsableAsync(providerId, ct);
        if (provider.IsError)
            return provider.Errors;
        var game = await ResolveAsync(provider.Value.Adapter, input, ct);
        return game.IsError ? InputErrors(game.Errors, "input") : game;
    }

    public async Task<ErrorOr<TitleProviderMatch>> SetAsync(int titleId, string providerId, string externalId,
        RevisionPrecondition ifMatch, CancellationToken ct)
    {
        var provider = await WritableAsync(titleId, providerId, ct);
        if (provider.IsError)
            return provider.Errors;
        // A stale curator fails before the provider is reached; the write compares again under the title lock.
        if (!ifMatch.IsSatisfiedBy(await _store.ReadRevisionAsync(titleId, providerId, ct)))
            return ProviderMatchErrors.PreconditionFailed;
        var game = await ResolveAsync(provider.Value.Adapter, externalId, ct);
        if (game.IsError)
            return InputErrors(game.Errors, "externalId");
        return await ChangeAsync(titleId, provider.Value, ifMatch, game.Value, false, ct);
    }

    public async Task<ErrorOr<TitleProviderMatch>> ConfirmAsync(int titleId, string providerId, RevisionPrecondition ifMatch,
        CancellationToken ct)
    {
        var provider = await WritableAsync(titleId, providerId, ct);
        return provider.IsError ? provider.Errors : await ChangeAsync(titleId, provider.Value, ifMatch, null, false, ct);
    }

    public async Task<ErrorOr<TitleProviderMatch>> UnlinkAsync(int titleId, string providerId, RevisionPrecondition ifMatch,
        CancellationToken ct)
    {
        var provider = await WritableAsync(titleId, providerId, ct);
        return provider.IsError ? provider.Errors : await ChangeAsync(titleId, provider.Value, ifMatch, null, true, ct);
    }

    /// <summary>The title, then a provider usable for a write.</summary>
    private async Task<ErrorOr<UsableProvider>> WritableAsync(int titleId, string providerId, CancellationToken ct) =>
        await _store.ExistsAsync(titleId, ct) ? await UsableAsync(providerId, ct) : CatalogErrors.TitleNotFound();

    /// <summary>
    ///     An enabled, configured provider. An unknown one is not found; one switched off or without credentials is
    ///     local configuration, a conflict rather than an unavailable upstream.
    /// </summary>
    private async Task<ErrorOr<UsableProvider>> UsableAsync(string providerId, CancellationToken ct)
    {
        if (Adapter(providerId) is not { } adapter)
            return ProviderMatchErrors.UnknownProvider;
        var availability = await adapter.GetAvailabilityAsync(ct);
        return availability is { Enabled: true, Configured: true }
            ? new UsableProvider(adapter, availability)
            : ProviderMatchErrors.NotConfigured;
    }

    /// <summary>Resolves one game by id or page URL; one the provider does not have is the caller's invalid input.</summary>
    private static async Task<ErrorOr<ProviderGame>> ResolveAsync(IProviderIdentityAdapter adapter, string idOrUrl,
        CancellationToken ct)
    {
        var game = await adapter.ResolveAsync(idOrUrl, ct);
        if (game.IsError && game.FirstError.Type == ErrorType.NotFound)
            return ProviderMatchErrors.Unresolvable;
        return game;
    }

    /// <summary>
    ///     The provider's rejection of the caller's id, page URL or query, keyed by the request field that carried it
    ///     (<see cref="FieldErrors" />). Every other failure passes through unchanged.
    /// </summary>
    private static List<Error> InputErrors(IReadOnlyList<Error> errors, string field) =>
    [
        .. errors.Select(error => error.Type == ErrorType.Validation
            ? FieldErrors.Validation(error.Code, error.Description, field)
            : error)
    ];

    private async Task<ErrorOr<TitleProviderMatch>> ChangeAsync(int titleId, UsableProvider provider,
        RevisionPrecondition ifMatch, ProviderGame? game, bool unlink, CancellationToken ct)
    {
        var written = await _unitOfWork.ExecuteInTransactionAsync<TitleProviderMatch>(async token =>
        {
            var changed = await _store.StageChangeAsync(titleId, provider.Adapter.Id, ifMatch, game, unlink, token);
            if (changed.IsError)
                return changed.Errors;
            Guid? pendingJobId = unlink ? null : await QueueFollowUpAsync(titleId, token);
            await _unitOfWork.FlushAsync(token);
            return BuildRow(provider.Adapter, provider.Availability,
                await _store.ReadFactsAsync(titleId, [provider.Adapter.Id], token), null, pendingJobId);
        }, _logger, ct);
        if (written.IsError)
            return written.Errors;
        return await ResolvePreviewAsync(provider.Adapter, provider.Availability, written.Value, ct, committed: true);
    }

    private async Task<Guid> QueueFollowUpAsync(int titleId, CancellationToken ct)
    {
        if (_completion is not null && await _completion.ResumeTitleStagedAsync(titleId, ct) is { } resumed)
            return resumed;
        if (await _store.FindPendingEnrichmentAsync(titleId, ct) is { } pending)
            return pending;
        var title = await _store.ReadTitleAsync(titleId, ct);
        var job = EnrichmentJob.Create(title.Name, title.Id, title.PlatformId, _clock.GetUtcNow(), artworkOnly: false);
        await _store.AddEnrichmentStagedAsync(job, ct);
        return job.Id;
    }

    private async Task<TitleProviderMatch> RowAsync(IProviderIdentityAdapter adapter, ProviderAvailability availability,
        TitleProviderMatchFacts facts, string? trustedGameId, Guid? pendingJobId, CancellationToken ct) =>
        await ResolvePreviewAsync(adapter, availability, BuildRow(adapter, availability, facts, trustedGameId, pendingJobId), ct);

    private Task<string?> ReadTrustedIdentityAsync(int titleId, string providerId, CancellationToken ct) =>
        _identityStore?.ReadTrustedAsync(titleId, providerId, ct) ?? Task.FromResult<string?>(null);

    private async Task<TitleProviderMatch> ResolvePreviewAsync(IProviderIdentityAdapter adapter,
        ProviderAvailability availability, TitleProviderMatch row, CancellationToken ct, bool committed = false)
    {
        if (row.Game is not null || row.ExternalId is null || !availability.Configured)
            return row;
        try
        {
            var resolved = await adapter.ResolveAsync(row.ExternalId, ct);
            return resolved.IsError ? row with { LastError = "Preview unavailable" } : row with { Game = resolved.Value };
        }
        catch (Exception ex) when (committed)
        {
            // A live preview is optional; it cannot undo the already committed match.
            _logger.LogWarning(ex, "Provider match committed but preview unavailable for provider {ProviderId}", adapter.Id);
            return row with { LastError = "Preview unavailable" };
        }
    }

    private static TitleProviderMatch BuildRow(IProviderIdentityAdapter adapter, ProviderAvailability availability,
        TitleProviderMatchFacts facts, string? trustedGameId, Guid? pendingJobId)
    {
        var link = facts.Links.SingleOrDefault(x => x.Provider.Equals(adapter.Id, StringComparison.OrdinalIgnoreCase));
        var state = facts.States.SingleOrDefault(x => x.ProviderId == adapter.Id);
        var game = state?.Game;
        if (link is not null && game?.Id != link.ExternalId)
            game = null;
        var payload = facts.Layers.SingleOrDefault(x => x.SourceId == adapter.Id)?.GetPayload();
        var stale = link is not null && adapter.Capabilities.Contains("metadata") &&
            (payload is null || (payload.ProviderGameId is { } origin
                ? origin != link.ExternalId : state?.IdentityChanged == true));
        return new(adapter.Id, adapter.Name, availability.Configured, adapter.Capabilities,
            ProviderMatchRevisions.For(state?.Revision, link?.ExternalId, link?.IsConfirmed), link is null ? (state?.SuppressAutomaticMatch == true ? "Unlinked" :
                state?.Evidence == "Provider search temporarily unavailable." ? "Unavailable" :
                game is not null || state?.Evidence?.StartsWith("Multiple games", StringComparison.Ordinal) == true ? "NeedsReview" : state is not null ? "NoMatchFound" : "NeedsMatch") :
            link.IsConfirmed ? "Confirmed" : trustedGameId == link.ExternalId ? "AutoMatched" : "NeedsReview", link?.ExternalId, game,
            link?.MatchConfidence, null, stale, state?.Evidence, state?.MatcherVersion, state?.Decision,
            pendingJobId);
    }

    private sealed record UsableProvider(IProviderIdentityAdapter Adapter, ProviderAvailability Availability);
}
