using System.Collections.ObjectModel;
using Romd.Domain.Catalog.Ratings;

namespace Romd.Domain.Catalog;

/// <summary>
///     Represents a canonical game title that groups DatGames across DAT revisions.
///     Titles are platform-specific and matched by normalized name.
///     This is the aggregate root for title-related entities (ExternalIds, Media, MetadataLayers).
/// </summary>
public sealed class Title
{
    /// <summary>
    ///     Field key used for per-board rating resolution in overrides and platform defaults.
    /// </summary>
    public const string ContentRatingsFieldName = "ContentRatings";

    private readonly List<TitleExternalId> _externalIds = [];
    private readonly List<TitleMedia> _media = [];
    private readonly List<TitleMetadataLayer> _metadataLayers = [];
    private readonly List<ContentRating> _contentRatings = [];
    private readonly Dictionary<string, string> _fieldProvenance;
    private readonly Dictionary<string, string> _fieldSourceOverrides;
    private readonly ReadOnlyCollection<TitleExternalId> _externalIdsView;
    private readonly ReadOnlyCollection<TitleMedia> _mediaView;
    private readonly ReadOnlyCollection<TitleMetadataLayer> _metadataLayersView;
    private readonly ReadOnlyCollection<ContentRating> _contentRatingsView;
    private readonly ReadOnlyDictionary<string, string> _fieldProvenanceView;
    private readonly ReadOnlyDictionary<string, string> _fieldSourceOverridesView;

    private Title(
        int id,
        int platformId,
        string name,
        string normalizedName,
        string? description,
        string? publisher,
        string? developer,
        string? genre,
        DateOnly? releaseDate,
        int? players,
        double? rating,
        EnrichmentStatus enrichmentStatus,
        DateTimeOffset? lastEnrichedAt,
        DateTimeOffset createdAt,
        IEnumerable<TitleExternalId>? externalIds,
        IEnumerable<TitleMedia>? media,
        IEnumerable<TitleMetadataLayer>? metadataLayers,
        Dictionary<string, string>? fieldProvenance,
        Dictionary<string, string>? fieldSourceOverrides,
        IEnumerable<ContentRating>? contentRatings,
        int? conservativeMinimumAge,
        Guid revision)
    {
        _externalIdsView = _externalIds.AsReadOnly();
        _mediaView = _media.AsReadOnly();
        _metadataLayersView = _metadataLayers.AsReadOnly();
        _contentRatingsView = _contentRatings.AsReadOnly();
        _fieldProvenance = fieldProvenance is null ? new() : new(fieldProvenance);
        _fieldSourceOverrides = fieldSourceOverrides is null ? new() : new(fieldSourceOverrides);
        _fieldProvenanceView = new ReadOnlyDictionary<string, string>(_fieldProvenance);
        _fieldSourceOverridesView = new ReadOnlyDictionary<string, string>(_fieldSourceOverrides);
        Revision = revision;
        Id = id;
        PlatformId = platformId;
        Name = name;
        NormalizedName = normalizedName;
        Description = description;
        Publisher = publisher;
        Developer = developer;
        Genre = genre;
        ReleaseDate = releaseDate;
        Players = players;
        Rating = rating;
        ConservativeMinimumAge = conservativeMinimumAge;
        EnrichmentStatus = enrichmentStatus;
        LastEnrichedAt = lastEnrichedAt;
        CreatedAt = createdAt;
        if (externalIds != null)
            _externalIds.AddRange(externalIds);
        if (media != null)
            _media.AddRange(media.Select(item => item.CopyForTitle(id)));
        if (metadataLayers != null)
            _metadataLayers.AddRange(metadataLayers);
        if (contentRatings != null)
        {
            _contentRatings.AddRange(contentRatings);
            ContentRatingsMaterialized = true;
        }
    }

    /// <summary>Persistence revision of this snapshot; reload after a committed mutation.</summary>
    public Guid Revision { get; private set; }

    public int Id { get; private set; }
    public int PlatformId { get; private set; }

    /// <summary>
    ///     Display name for the title (taken from first matched DatGame).
    /// </summary>
    public string Name { get; private set; }

    /// <summary>
    ///     Normalized name for matching: lowercase, no regions/revisions, alphanumeric only.
    /// </summary>
    public string NormalizedName { get; private set; }

    // Enrichment metadata (nullable until scraped from metadata providers)
    public string? Description { get; private set; }
    public string? Publisher { get; private set; }
    public string? Developer { get; private set; }
    public string? Genre { get; private set; }
    public DateOnly? ReleaseDate { get; private set; }
    public int? Players { get; private set; }
    public double? Rating { get; private set; }

    /// <summary>
    ///     Max <see cref="Ratings.ContentRating.MinimumAge" /> across rated boards;
    ///     null when no board has a Rated rating. Denormalized for catalog filtering.
    /// </summary>
    public int? ConservativeMinimumAge { get; private set; }

    // Enrichment tracking
    public EnrichmentStatus EnrichmentStatus { get; private set; }
    public DateTimeOffset? LastEnrichedAt { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    /// <summary>
    ///     External IDs from metadata providers (IGDB, ScreenScraper, etc.).
    /// </summary>
    public IReadOnlyCollection<TitleExternalId> ExternalIds => _externalIdsView;

    /// <summary>
    ///     Media assets (cover art, screenshots, videos, etc.).
    /// </summary>
    public IReadOnlyCollection<TitleMedia> Media => _mediaView;

    /// <summary>
    ///     Metadata layers from different sources (user, igdb, screenscraper, etc.).
    /// </summary>
    public IReadOnlyCollection<TitleMetadataLayer> MetadataLayers => _metadataLayersView;

    /// <summary>
    ///     Effective per-board content ratings, one per board, resolved from layer claims
    ///     during <see cref="Rematerialize" />. Each row carries its own provenance.
    /// </summary>
    public IReadOnlyCollection<ContentRating> ContentRatings => _contentRatingsView;

    /// <summary>
    ///     True when <see cref="ContentRatings" /> reflects authoritative state — either
    ///     loaded from persistence or recomputed by <see cref="Rematerialize" />. Repositories
    ///     must only sync rating rows when this is set; otherwise an instance loaded without
    ///     its rating collection would wipe persisted rows on update.
    /// </summary>
    public bool ContentRatingsMaterialized { get; private set; }

    /// <summary>
    ///     Tracks which source provided each effective field value.
    ///     Keys are property names, values are source IDs.
    /// </summary>
    public IReadOnlyDictionary<string, string> FieldProvenance => _fieldProvenanceView;

    /// <summary>
    ///     Per-field user overrides specifying which source to use.
    ///     Keys are property names, values are source IDs.
    /// </summary>
    public IReadOnlyDictionary<string, string> FieldSourceOverrides => _fieldSourceOverridesView;

    /// <summary>
    ///     Queues this title for enrichment.
    /// </summary>
    public void QueueForEnrichment()
    {
        if (EnrichmentStatus == EnrichmentStatus.None)
        {
            EnrichmentStatus = EnrichmentStatus.Pending;
        }
    }

    /// <summary>
    ///     Re-queues the title for provider enrichment regardless of current status.
    ///     Used when stored metadata is known to need refreshing (e.g. migration backfills).
    /// </summary>
    public void RequeueForEnrichment()
    {
        EnrichmentStatus = EnrichmentStatus.Pending;
    }

    /// <summary>
    ///     Marks enrichment as completed.
    /// </summary>
    public void MarkEnrichmentCompleted(DateTimeOffset occurredAt)
    {
        EnrichmentStatus = EnrichmentStatus.Completed;
        LastEnrichedAt = occurredAt;
    }

    /// <summary>
    ///     Marks enrichment as failed.
    /// </summary>
    public void MarkEnrichmentFailed(DateTimeOffset occurredAt)
    {
        EnrichmentStatus = EnrichmentStatus.Failed;
        LastEnrichedAt = occurredAt;
    }

    /// <summary>
    ///     Marks enrichment as not found (no match in any provider).
    /// </summary>
    public void MarkEnrichmentNotFound(DateTimeOffset occurredAt)
    {
        EnrichmentStatus = EnrichmentStatus.NotFound;
        LastEnrichedAt = occurredAt;
    }

    /// <summary>
    ///     Marks enrichment as low confidence (match found but below threshold).
    /// </summary>
    public void MarkEnrichmentLowConfidence(DateTimeOffset occurredAt)
    {
        EnrichmentStatus = EnrichmentStatus.LowConfidence;
        LastEnrichedAt = occurredAt;
    }

    /// <summary>
    ///     Creates a new title with validated invariants.
    /// </summary>
    /// <exception cref="ArgumentException">Thrown when name or normalizedName is null or whitespace.</exception>
    public static Title CreateNew(int platformId, string name, string normalizedName, DateTimeOffset createdAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(normalizedName);

        return new Title(
            id: 0,
            platformId: platformId,
            name: name,
            normalizedName: normalizedName,
            description: null,
            publisher: null,
            developer: null,
            genre: null,
            releaseDate: null,
            players: null,
            rating: null,
            enrichmentStatus: EnrichmentStatus.None,
            lastEnrichedAt: null,
            createdAt: createdAt,
            externalIds: null,
            media: null,
            metadataLayers: null,
            fieldProvenance: null,
            fieldSourceOverrides: null,
            contentRatings: null,
            conservativeMinimumAge: null,
            revision: Guid.NewGuid());
    }

    /// <summary>
    ///     Rehydrates a title from persistence. Trusts that data is valid.
    ///     Collections are optional - pass null for queries that don't load them.
    /// </summary>
    internal static Title Rehydrate(
        int id,
        int platformId,
        string name,
        string normalizedName,
        string? description,
        string? publisher,
        string? developer,
        string? genre,
        DateOnly? releaseDate,
        int? players,
        double? rating,
        EnrichmentStatus enrichmentStatus,
        DateTimeOffset? lastEnrichedAt,
        DateTimeOffset createdAt,
        IEnumerable<TitleExternalId>? externalIds = null,
        IEnumerable<TitleMedia>? media = null,
        IEnumerable<TitleMetadataLayer>? metadataLayers = null,
        Dictionary<string, string>? fieldProvenance = null,
        Dictionary<string, string>? fieldSourceOverrides = null,
        IEnumerable<ContentRating>? contentRatings = null,
        int? conservativeMinimumAge = null,
        Guid revision = default)
    {
        return new Title(
            id, platformId, name, normalizedName, description, publisher, developer, genre,
            releaseDate, players, rating, enrichmentStatus, lastEnrichedAt,
            createdAt, externalIds, media, metadataLayers, fieldProvenance,
            fieldSourceOverrides, contentRatings, conservativeMinimumAge, revision);
    }

    #region External ID Management

    /// <summary>
    ///     Gets the external ID for a specific provider.
    /// </summary>
    public TitleExternalId? GetExternalId(string provider)
    {
        return _externalIds.FirstOrDefault(e =>
            e.Provider.Equals(provider, StringComparison.OrdinalIgnoreCase));
    }

    #endregion

    #region Media Management

    /// <summary>
    ///     Gets the primary media for a specific type.
    /// </summary>
    public TitleMedia? GetPrimaryMedia(MediaType type) => _media.FirstOrDefault(m => m.Type == type && m.IsPrimary);

    /// <summary>
    ///     Gets all media of a specific type.
    /// </summary>
    public IEnumerable<TitleMedia> GetMediaByType(MediaType type) => _media.Where(m => m.Type == type);

    /// <summary>
    ///     Gets media by its ID.
    /// </summary>
    public TitleMedia? GetMediaById(int mediaId) => _media.FirstOrDefault(m => m.Id == mediaId);

    /// <summary>
    ///     Gets media by type and source.
    /// </summary>
    public TitleMedia? GetMediaByTypeAndSource(MediaType type, string sourceId) =>
        _media.FirstOrDefault(m => m.Type == type && m.SourceId.Equals(sourceId, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    ///     Checks if media of a specific type exists.
    /// </summary>
    public bool HasMedia(MediaType type) => _media.Any(m => m.Type == type);

    /// <summary>
    ///     Adds media to the title. Does NOT automatically set IsPrimary;
    ///     caller should invoke RecalculatePrimaryMedia after adding.
    /// </summary>
    public void AddMedia(TitleMedia media)
    {
        ArgumentNullException.ThrowIfNull(media);
        _media.Add(media.CopyForTitle(Id));
    }

    /// <summary>
    ///     Adds or replaces media for a specific (type, sourceId) combination.
    ///     This enforces idempotency: one media per (Type, SourceId).
    ///     Returns the removed media if any, for cleanup.
    /// </summary>
    public TitleMedia? AddOrReplaceMedia(TitleMedia media)
    {
        ArgumentNullException.ThrowIfNull(media);
        var owned = media.CopyForTitle(Id);
        var existing = GetMediaByTypeAndSource(owned.Type, owned.SourceId);
        if (existing != null)
        {
            _media.Remove(existing);
        }

        _media.Add(owned);
        return existing;
    }

    /// <summary>
    ///     Removes media by ID. Returns true if found and removed.
    ///     Caller should invoke RecalculatePrimaryMedia if the removed media was primary.
    /// </summary>
    public bool RemoveMedia(int mediaId)
    {
        var existing = GetMediaById(mediaId);
        if (existing != null)
        {
            return _media.Remove(existing);
        }

        return false;
    }

    /// <summary>
    ///     Manually sets a specific media as primary. Clears other primaries of the same type.
    /// </summary>
    public void SetPrimaryMedia(int mediaId)
    {
        var media = GetMediaById(mediaId);
        if (media == null)
            return;

        // Clear other primaries of this type
        foreach (var m in _media.Where(m => m.Type == media.Type))
        {
            m.SetPrimary(ReferenceEquals(m, media));
        }
    }

    /// <summary>
    ///     Recalculates primary media for all types based on priority.
    ///     Priority: User uploads ("user") > Provider priority order > Oldest by CreatedAt.
    /// </summary>
    public void RecalculatePrimaryMedia(IReadOnlyList<string> sourcePriorityOrder)
    {
        var mediaTypes = _media.Select(m => m.Type).Distinct();

        foreach (var type in mediaTypes)
        {
            var candidates = _media.Where(m => m.Type == type).ToList();

            // Clear all primaries first
            foreach (var m in candidates)
            {
                m.SetPrimary(false);
            }

            if (candidates.Count == 0)
                continue;

            // Priority selection:
            // 1. User uploads (SourceId = "user") always win
            var userMedia = candidates.FirstOrDefault(m =>
                m.SourceId.Equals("user", StringComparison.OrdinalIgnoreCase));
            if (userMedia != null)
            {
                userMedia.SetPrimary(true);
                continue;
            }

            // 2. Sort by provider priority, then by CreatedAt (oldest first for stability)
            var best = candidates
                .OrderBy(m =>
                {
                    var index = -1;
                    for (var i = 0; i < sourcePriorityOrder.Count; i++)
                    {
                        if (sourcePriorityOrder[i].Equals(m.SourceId, StringComparison.OrdinalIgnoreCase))
                        {
                            index = i;
                            break;
                        }
                    }

                    return index == -1 ? int.MaxValue : index;
                })
                .ThenBy(m => m.CreatedAt)
                .First();

            best.SetPrimary(true);
        }
    }

    #endregion

    #region Metadata Layer Management

    /// <summary>
    ///     Gets the metadata layer for a specific source.
    /// </summary>
    public TitleMetadataLayer? GetMetadataLayer(string sourceId)
    {
        return _metadataLayers.FirstOrDefault(l =>
            l.SourceId.Equals(sourceId, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    ///     Stores a provider metadata layer WITHOUT rematerializing.
    ///     Caller must invoke Rematerialize separately.
    /// </summary>
    public void StoreProviderLayer(string sourceId, MetadataSourceType sourceType, TitleMetadataPayload payload, DateTimeOffset updatedAt)
    {
        var layer = GetOrCreateLayer(sourceId, sourceType, updatedAt);
        layer.UpdateData(sourceType == MetadataSourceType.Provider
            ? payload with { ProviderGameId = payload.ProviderGameId ?? GetExternalId(sourceId)?.ExternalId }
            : payload, updatedAt);
    }

    /// <summary>
    ///     Stores user metadata layer WITHOUT rematerializing.
    ///     Caller must invoke Rematerialize separately.
    /// </summary>
    public void StoreUserLayer(TitleMetadataPayload payload, DateTimeOffset updatedAt)
    {
        var layer = GetOrCreateLayer("user", MetadataSourceType.User, updatedAt);
        layer.UpdateData(payload, updatedAt);
    }

    /// <summary>
    ///     Adds or replaces the user-layer content rating claim for a single board, preserving
    ///     the user layer's other fields and the claims it asserts for other boards. The claim
    ///     is stored raw; canonicalization and cascade resolution happen in <see cref="Rematerialize" />.
    ///     Caller must invoke <see cref="Rematerialize" /> separately.
    /// </summary>
    public void SetUserContentRating(ContentRatingClaim claim, DateTimeOffset updatedAt)
    {
        ArgumentNullException.ThrowIfNull(claim);

        var existing = GetMetadataLayer("user")?.GetPayload() ?? new TitleMetadataPayload();
        var claims = (existing.ContentRatings ?? [])
            .Where(c => c.Board != claim.Board)
            .Append(claim)
            .ToList();

        StoreUserLayer(existing with { ContentRatings = claims }, updatedAt);
    }

    /// <summary>
    ///     Removes the user-layer content rating claim for a board, reverting that board to the
    ///     provider cascade. Preserves the user layer's other fields and board claims.
    ///     Caller must invoke <see cref="Rematerialize" /> separately.
    /// </summary>
    public void ClearUserContentRating(RatingBoard board, DateTimeOffset updatedAt)
    {
        var existing = GetMetadataLayer("user")?.GetPayload();
        if (existing?.ContentRatings is not { Count: > 0 } current)
        {
            return;
        }

        var remaining = current.Where(c => c.Board != board).ToList();
        StoreUserLayer(existing with { ContentRatings = remaining.Count > 0 ? remaining : null }, updatedAt);
    }

    /// <summary>
    ///     Sets a per-field source override. When set, the specified field will always
    ///     use data from the specified source, regardless of global priority.
    /// </summary>
    public void SetFieldSourceOverride(string fieldName, string sourceId)
    {
        _fieldSourceOverrides[fieldName] = sourceId;
    }

    /// <summary>
    ///     Clears a per-field source override, reverting to priority-based resolution.
    /// </summary>
    public void ClearFieldSourceOverride(string fieldName)
    {
        _fieldSourceOverrides.Remove(fieldName);
    }

    /// <summary>
    ///     Recomputes all materialized fields from metadata layers using the resolution cascade:
    ///     1. Title-level field override (from _fieldSourceOverrides)
    ///     2. User layer
    ///     3. Platform-level defaults (from platformDefaults parameter)
    ///     4. Global source priority order
    ///     5. Remaining providers, newest first with stable ties
    ///     Content ratings resolve through the same cascade per board: for each board, the
    ///     highest-priority layer asserting a recognizable claim for that board wins.
    /// </summary>
    public void Rematerialize(
        IReadOnlyList<string> globalSourcePriority,
        IReadOnlyDictionary<string, string>? platformDefaults = null)
    {
        // Decode and capture every relevant input before changing effective state. Additional user
        // layers are ignored, matching the first-user-layer cascade even if their stored JSON is invalid.
        var userLayer = _metadataLayers.FirstOrDefault(layer => layer.SourceId.Equals("user", StringComparison.OrdinalIgnoreCase));
        var layers = _metadataLayers.Where(layer => !layer.SourceId.Equals("user", StringComparison.OrdinalIgnoreCase)
                || ReferenceEquals(layer, userLayer))
            .Select(layer => new TitleMetadataLayerFacts(layer.SourceId, layer.UpdatedAt, layer.GetPayload()));
        var input = new TitleMetadataResolutionInput(layers, globalSourcePriority, _fieldSourceOverrides, platformDefaults);
        var resolution = TitleMetadataResolutionPolicy.Resolve(input);
        ApplyMetadataResolution(resolution);
    }

    private void ApplyMetadataResolution(TitleMetadataResolution resolution)
    {
        Description = resolution.Description;
        Publisher = resolution.Publisher;
        Developer = resolution.Developer;
        Genre = resolution.Genre;
        ReleaseDate = resolution.ReleaseDate;
        Players = resolution.Players;
        Rating = resolution.Rating;
        _fieldProvenance.Clear();
        foreach (var (field, source) in resolution.FieldProvenance)
            _fieldProvenance.Add(field, source);
        _contentRatings.Clear();
        _contentRatings.AddRange(resolution.ContentRatings);
        ConservativeMinimumAge = resolution.ConservativeMinimumAge;
        ContentRatingsMaterialized = true;
    }

    private TitleMetadataLayer GetOrCreateLayer(string sourceId, MetadataSourceType sourceType, DateTimeOffset updatedAt)
    {
        var existing = GetMetadataLayer(sourceId);
        if (existing != null)
        {
            return existing;
        }

        var newLayer = TitleMetadataLayer.CreateNew(Id, sourceId, sourceType, new TitleMetadataPayload(), updatedAt);
        _metadataLayers.Add(newLayer);
        return newLayer;
    }

    #endregion

    #region Merge Operations

    /// <summary>
    ///     Absorbs another title's metadata layers, external IDs, and media.
    ///     Target (this) wins on conflicts; source fills gaps only. A provider in
    ///     <paramref name="unlinkedProviders" />, which the curator unlinked on the target, stays unlinked: neither
    ///     its link, its metadata layers nor the media enrichment sourced from it (a source id that is exactly the
    ///     provider) is absorbed, so no effective field comes from it. An unlink keeps curator-collected gallery
    ///     images (<c>gallery:&lt;providerId&gt;</c>), so those are absorbed like any other media.
    /// </summary>
    public void Absorb(Title source, IReadOnlyList<string> sourcePriorityOrder,
        IReadOnlyCollection<string> unlinkedProviders, TitleAbsorptionTimes times)
    {
        ArgumentNullException.ThrowIfNull(times);
        bool Unlinked(string provider) => unlinkedProviders.Contains(provider, StringComparer.OrdinalIgnoreCase);

        foreach (var layer in source.MetadataLayers.Where(layer => !Unlinked(layer.SourceId) && GetMetadataLayer(layer.SourceId) is null))
            if (!times.MetadataLayerUpdatedAt.ContainsKey(layer.SourceId))
                throw new ArgumentException($"Missing absorption timestamp for metadata source '{layer.SourceId}'.", nameof(times));

        // 1. Merge external IDs (source fills gaps only; confirmed target links are never overwritten)
        foreach (var sourceExtId in source.ExternalIds.Where(link => !Unlinked(link.Provider)))
        {
            var existing = GetExternalId(sourceExtId.Provider);
            if (existing is null)
            {
                _externalIds.Add(TitleExternalId.CreateNew(
                    Id,
                    sourceExtId.Provider,
                    sourceExtId.ExternalId,
                    sourceExtId.MatchConfidence, times.CreatedAt));
            }
            // If target has a confirmed link for this provider, skip source's link
        }

        // 2. Merge metadata layers (source fills gaps only)
        foreach (var sourceLayer in source.MetadataLayers.Where(layer => !Unlinked(layer.SourceId)))
        {
            var existing = GetMetadataLayer(sourceLayer.SourceId);
            if (existing is null)
            {
                var payload = sourceLayer.GetPayload() ?? new TitleMetadataPayload();
                var newLayer = TitleMetadataLayer.CreateNew(
                    Id,
                    sourceLayer.SourceId,
                    sourceLayer.SourceType,
                    payload, times.MetadataLayerUpdatedAt[sourceLayer.SourceId]);
                _metadataLayers.Add(newLayer);
            }
        }

        // 3. Merge media (source fills gaps only, respecting unique constraint)
        foreach (var sourceMedia in source.Media.Where(media => !Unlinked(media.SourceId)))
        {
            var existing = sourceMedia.SourceId == "user"
                ? _media.FirstOrDefault(media => media.Type == sourceMedia.Type && media.SourceId == "user" && media.FileId == sourceMedia.FileId)
                : GetMediaByTypeAndSource(sourceMedia.Type, sourceMedia.SourceId);
            if (existing is null)
            {
                var media = TitleMedia.CreateNew(
                    Id,
                    sourceMedia.Type,
                    sourceMedia.FileId,
                    sourceMedia.SourceId,
                    sourceMedia.ContentType,
                    times.CreatedAt,
                    sourceMedia.SourceUrl);
                _media.Add(media);
            }
        }

        // 4. Recalculate effective states
        Rematerialize(sourcePriorityOrder);
        RecalculatePrimaryMedia(sourcePriorityOrder);
    }

    /// <summary>
    ///     Inherits enrichment status from source if target hasn't been enriched.
    /// </summary>
    public void InheritEnrichmentStatus(EnrichmentStatus sourceStatus, DateTimeOffset? sourceLastEnrichedAt)
    {
        if (EnrichmentStatus == EnrichmentStatus.None && sourceStatus == EnrichmentStatus.Completed)
        {
            EnrichmentStatus = sourceStatus;
            LastEnrichedAt = sourceLastEnrichedAt;
        }
    }

    #endregion
}
