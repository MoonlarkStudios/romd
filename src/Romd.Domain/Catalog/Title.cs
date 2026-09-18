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
    private Dictionary<string, string> _fieldProvenance = new();
    private Dictionary<string, string> _fieldSourceOverrides = new();

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
        if (externalIds != null) _externalIds.AddRange(externalIds);
        if (media != null) _media.AddRange(media);
        if (metadataLayers != null) _metadataLayers.AddRange(metadataLayers);
        if (fieldProvenance != null) _fieldProvenance = new Dictionary<string, string>(fieldProvenance);
        if (fieldSourceOverrides != null) _fieldSourceOverrides = new Dictionary<string, string>(fieldSourceOverrides);
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
    public IReadOnlyCollection<TitleExternalId> ExternalIds => _externalIds;

    /// <summary>
    ///     Media assets (cover art, screenshots, videos, etc.).
    /// </summary>
    public IReadOnlyCollection<TitleMedia> Media => _media;

    /// <summary>
    ///     Metadata layers from different sources (user, igdb, screenscraper, etc.).
    /// </summary>
    public IReadOnlyCollection<TitleMetadataLayer> MetadataLayers => _metadataLayers;

    /// <summary>
    ///     Effective per-board content ratings, one per board, resolved from layer claims
    ///     during <see cref="Rematerialize" />. Each row carries its own provenance.
    /// </summary>
    public IReadOnlyCollection<ContentRating> ContentRatings => _contentRatings;

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
    public IReadOnlyDictionary<string, string> FieldProvenance => _fieldProvenance;

    /// <summary>
    ///     Per-field user overrides specifying which source to use.
    ///     Keys are property names, values are source IDs.
    /// </summary>
    public IReadOnlyDictionary<string, string> FieldSourceOverrides => _fieldSourceOverrides;

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
    public void MarkEnrichmentCompleted()
    {
        EnrichmentStatus = EnrichmentStatus.Completed;
        LastEnrichedAt = DateTimeOffset.UtcNow;
    }

    /// <summary>
    ///     Marks enrichment as failed.
    /// </summary>
    public void MarkEnrichmentFailed()
    {
        EnrichmentStatus = EnrichmentStatus.Failed;
        LastEnrichedAt = DateTimeOffset.UtcNow;
    }

    /// <summary>
    ///     Marks enrichment as not found (no match in any provider).
    /// </summary>
    public void MarkEnrichmentNotFound()
    {
        EnrichmentStatus = EnrichmentStatus.NotFound;
        LastEnrichedAt = DateTimeOffset.UtcNow;
    }

    /// <summary>
    ///     Marks enrichment as low confidence (match found but below threshold).
    /// </summary>
    public void MarkEnrichmentLowConfidence()
    {
        EnrichmentStatus = EnrichmentStatus.LowConfidence;
        LastEnrichedAt = DateTimeOffset.UtcNow;
    }

    /// <summary>
    ///     Creates a new title with validated invariants.
    /// </summary>
    /// <exception cref="ArgumentException">Thrown when name or normalizedName is null or whitespace.</exception>
    public static Title CreateNew(int platformId, string name, string normalizedName)
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
            createdAt: DateTimeOffset.UtcNow,
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

    /// <summary>
    ///     Adds or updates an external ID from auto-enrichment.
    ///     Creates unconfirmed or calls TryUpdateAutoMatch (no-op if confirmed).
    /// </summary>
    /// <returns>True if the external ID was created or updated, false if rejected (confirmed).</returns>
    public bool SetExternalIdFromAutoEnrichment(string provider, string externalId, float matchConfidence)
    {
        var existing = GetExternalId(provider);
        if (existing != null)
        {
            return existing.TryUpdateAutoMatch(externalId, matchConfidence);
        }

        _externalIds.Add(TitleExternalId.CreateNew(Id, provider, externalId, matchConfidence));
        return true;
    }

    /// <summary>
    ///     Adds or updates an external ID manually by a user.
    ///     Always succeeds and marks as confirmed with confidence 1.0.
    /// </summary>
    public void SetExternalIdManually(string provider, string externalId)
    {
        var existing = GetExternalId(provider);
        if (existing != null)
        {
            existing.SetManually(externalId);
        }
        else
        {
            var ext = TitleExternalId.CreateNew(Id, provider, externalId, 1.0f);
            ext.Confirm();
            _externalIds.Add(ext);
        }
    }

    /// <summary>
    ///     Confirms an existing external ID for a provider, making it immutable to auto-enrichment.
    ///     If EnrichmentStatus is LowConfidence, transitions to Completed.
    /// </summary>
    /// <returns>True if the external ID was found and confirmed, false if no external ID exists for this provider.</returns>
    public bool ConfirmExternalId(string provider)
    {
        var existing = GetExternalId(provider);
        if (existing is null)
            return false;

        existing.Confirm();

        if (EnrichmentStatus == EnrichmentStatus.LowConfidence)
        {
            MarkEnrichmentCompleted();
        }

        return true;
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
        _media.Add(media);
    }

    /// <summary>
    ///     Adds or replaces media for a specific (type, sourceId) combination.
    ///     This enforces idempotency: one media per (Type, SourceId).
    ///     Returns the removed media if any, for cleanup.
    /// </summary>
    public TitleMedia? AddOrReplaceMedia(TitleMedia media)
    {
        var existing = GetMediaByTypeAndSource(media.Type, media.SourceId);
        if (existing != null)
        {
            _media.Remove(existing);
        }

        _media.Add(media);
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
        if (media == null) return;

        // Clear other primaries of this type
        foreach (var m in _media.Where(m => m.Type == media.Type))
        {
            m.SetPrimary(m.Id == mediaId);
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

            if (candidates.Count == 0) continue;

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
    public void StoreProviderLayer(string sourceId, MetadataSourceType sourceType, TitleMetadataPayload payload)
    {
        var layer = GetOrCreateLayer(sourceId, sourceType);
        layer.UpdateData(sourceType == MetadataSourceType.Provider
            ? payload with { ProviderGameId = GetExternalId(sourceId)?.ExternalId }
            : payload);
    }

    /// <summary>
    ///     Stores user metadata layer WITHOUT rematerializing.
    ///     Caller must invoke Rematerialize separately.
    /// </summary>
    public void StoreUserLayer(TitleMetadataPayload payload)
    {
        var layer = GetOrCreateLayer("user", MetadataSourceType.User);
        layer.UpdateData(payload);
    }

    /// <summary>
    ///     Adds or replaces the user-layer content rating claim for a single board, preserving
    ///     the user layer's other fields and the claims it asserts for other boards. The claim
    ///     is stored raw; canonicalization and cascade resolution happen in <see cref="Rematerialize" />.
    ///     Caller must invoke <see cref="Rematerialize" /> separately.
    /// </summary>
    public void SetUserContentRating(ContentRatingClaim claim)
    {
        ArgumentNullException.ThrowIfNull(claim);

        var existing = GetMetadataLayer("user")?.GetPayload() ?? new TitleMetadataPayload();
        var claims = (existing.ContentRatings ?? [])
            .Where(c => c.Board != claim.Board)
            .Append(claim)
            .ToList();

        StoreUserLayer(existing with { ContentRatings = claims });
    }

    /// <summary>
    ///     Removes the user-layer content rating claim for a board, reverting that board to the
    ///     provider cascade. Preserves the user layer's other fields and board claims.
    ///     Caller must invoke <see cref="Rematerialize" /> separately.
    /// </summary>
    public void ClearUserContentRating(RatingBoard board)
    {
        var existing = GetMetadataLayer("user")?.GetPayload();
        if (existing?.ContentRatings is not { Count: > 0 } current)
        {
            return;
        }

        var remaining = current.Where(c => c.Board != board).ToList();
        StoreUserLayer(existing with { ContentRatings = remaining.Count > 0 ? remaining : null });
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
    ///     2. Platform-level defaults (from platformDefaults parameter)
    ///     3. Global source priority order
    ///     4. First available (any layer with non-null value)
    ///     Content ratings resolve through the same cascade per board: for each board, the
    ///     highest-priority layer asserting a recognizable claim for that board wins.
    /// </summary>
    public void Rematerialize(
        IReadOnlyList<string> globalSourcePriority,
        IReadOnlyDictionary<string, string>? platformDefaults = null)
    {
        // Build ordered layer list for resolution
        var userLayer = _metadataLayers.FirstOrDefault(l =>
            l.SourceId.Equals("user", StringComparison.OrdinalIgnoreCase));

        var automatedLayers = _metadataLayers
            .Where(l => !l.SourceId.Equals("user", StringComparison.OrdinalIgnoreCase))
            .OrderBy(l =>
            {
                var index = -1;
                for (var i = 0; i < globalSourcePriority.Count; i++)
                {
                    if (globalSourcePriority[i].Equals(l.SourceId, StringComparison.OrdinalIgnoreCase))
                    {
                        index = i;
                        break;
                    }
                }

                return index == -1 ? int.MaxValue : index;
            })
            .ThenByDescending(l => l.UpdatedAt)
            .ToList();

        // Cache layer payloads
        var layerData = new Dictionary<string, TitleMetadataPayload?>(StringComparer.OrdinalIgnoreCase);
        if (userLayer != null) layerData["user"] = userLayer.GetPayload();
        foreach (var layer in automatedLayers)
        {
            layerData[layer.SourceId] = layer.GetPayload();
        }

        // Reset
        ResetEffectiveMetadata();
        _fieldProvenance.Clear();

        // Resolve each field individually (Name is excluded — it comes from DAT data)
        ResolveField(nameof(Description), p => p?.Description, v => Description = v!, globalSourcePriority, platformDefaults, layerData, userLayer, automatedLayers);
        ResolveField(nameof(Publisher), p => p?.Publisher, v => Publisher = v!, globalSourcePriority, platformDefaults, layerData, userLayer, automatedLayers);
        ResolveField(nameof(Developer), p => p?.Developer, v => Developer = v!, globalSourcePriority, platformDefaults, layerData, userLayer, automatedLayers);
        ResolveField(nameof(Genre), p => p?.Genre, v => Genre = v!, globalSourcePriority, platformDefaults, layerData, userLayer, automatedLayers);
        ResolveFieldValueType(nameof(ReleaseDate), p => p?.ReleaseDate, v => ReleaseDate = v, globalSourcePriority, platformDefaults, layerData, userLayer, automatedLayers);
        ResolveFieldValueType(nameof(Players), p => p?.Players, v => Players = v, globalSourcePriority, platformDefaults, layerData, userLayer, automatedLayers);
        ResolveFieldValueType(nameof(Rating), p => p?.Rating, v => Rating = v, globalSourcePriority, platformDefaults, layerData, userLayer, automatedLayers);

        ResolveContentRatings(platformDefaults, layerData, userLayer, automatedLayers);
    }

    /// <summary>
    ///     Resolves the effective per-board ratings from layer claims. The cascade order is
    ///     built once (override → user → platform default → global priority); each board takes
    ///     the first recognizable claim along it. Unrecognized claims are dropped, never
    ///     guessed — a lower-priority layer may then supply that board, mirroring the
    ///     null-fall-through semantics of scalar fields.
    /// </summary>
    private void ResolveContentRatings(
        IReadOnlyDictionary<string, string>? platformDefaults,
        Dictionary<string, TitleMetadataPayload?> layerData,
        TitleMetadataLayer? userLayer,
        List<TitleMetadataLayer> automatedLayers)
    {
        var orderedSourceIds = new List<string>();

        if (_fieldSourceOverrides.TryGetValue(ContentRatingsFieldName, out var overrideSource))
            orderedSourceIds.Add(overrideSource);

        if (userLayer != null)
            orderedSourceIds.Add("user");

        if (platformDefaults != null && platformDefaults.TryGetValue(ContentRatingsFieldName, out var platformSource))
            orderedSourceIds.Add(platformSource);

        orderedSourceIds.AddRange(automatedLayers.Select(l => l.SourceId));

        foreach (var sourceId in orderedSourceIds)
        {
            if (!layerData.TryGetValue(sourceId, out var payload) ||
                payload?.ContentRatings is not { Count: > 0 } claims)
            {
                continue;
            }

            foreach (var claim in claims)
            {
                if (_contentRatings.Any(r => r.Board == claim.Board))
                    continue;

                var resolved = RatingBoardCatalog.TryResolve(claim, sourceId);
                if (resolved != null)
                    _contentRatings.Add(resolved);
            }
        }

        ConservativeMinimumAge = _contentRatings
            .Where(r => r.Designation == RatingDesignation.Rated)
            .Max(r => r.MinimumAge);

        ContentRatingsMaterialized = true;
    }

    private void ResolveField(
        string fieldName,
        Func<TitleMetadataPayload?, string?> extractor,
        Action<string> setter,
        IReadOnlyList<string> globalPriority,
        IReadOnlyDictionary<string, string>? platformDefaults,
        Dictionary<string, TitleMetadataPayload?> layerData,
        TitleMetadataLayer? userLayer,
        List<TitleMetadataLayer> automatedLayers)
    {
        // 1. Title-level field override
        if (_fieldSourceOverrides.TryGetValue(fieldName, out var overrideSource))
        {
            if (layerData.TryGetValue(overrideSource, out var overridePayload))
            {
                var val = extractor(overridePayload);
                if (val != null)
                {
                    setter(val);
                    _fieldProvenance[fieldName] = overrideSource;
                    return;
                }
            }
        }

        // 2. User layer always wins (if no field-level override)
        if (userLayer != null && layerData.TryGetValue("user", out var userData))
        {
            var val = extractor(userData);
            if (val != null)
            {
                setter(val);
                _fieldProvenance[fieldName] = "user";
                return;
            }
        }

        // 3. Platform-level default
        if (platformDefaults != null && platformDefaults.TryGetValue(fieldName, out var platformSource))
        {
            if (layerData.TryGetValue(platformSource, out var platformPayload))
            {
                var val = extractor(platformPayload);
                if (val != null)
                {
                    setter(val);
                    _fieldProvenance[fieldName] = platformSource;
                    return;
                }
            }
        }

        // 4. Global priority order
        foreach (var layer in automatedLayers)
        {
            if (layerData.TryGetValue(layer.SourceId, out var payload))
            {
                var val = extractor(payload);
                if (val != null)
                {
                    setter(val);
                    _fieldProvenance[fieldName] = layer.SourceId;
                    return;
                }
            }
        }
    }

    private void ResolveFieldValueType<T>(
        string fieldName,
        Func<TitleMetadataPayload?, T?> extractor,
        Action<T> setter,
        IReadOnlyList<string> globalPriority,
        IReadOnlyDictionary<string, string>? platformDefaults,
        Dictionary<string, TitleMetadataPayload?> layerData,
        TitleMetadataLayer? userLayer,
        List<TitleMetadataLayer> automatedLayers) where T : struct
    {
        // 1. Title-level field override
        if (_fieldSourceOverrides.TryGetValue(fieldName, out var overrideSource))
        {
            if (layerData.TryGetValue(overrideSource, out var overridePayload))
            {
                var val = extractor(overridePayload);
                if (val.HasValue)
                {
                    setter(val.Value);
                    _fieldProvenance[fieldName] = overrideSource;
                    return;
                }
            }
        }

        // 2. User layer
        if (userLayer != null && layerData.TryGetValue("user", out var userData))
        {
            var val = extractor(userData);
            if (val.HasValue)
            {
                setter(val.Value);
                _fieldProvenance[fieldName] = "user";
                return;
            }
        }

        // 3. Platform-level default
        if (platformDefaults != null && platformDefaults.TryGetValue(fieldName, out var platformSource))
        {
            if (layerData.TryGetValue(platformSource, out var platformPayload))
            {
                var val = extractor(platformPayload);
                if (val.HasValue)
                {
                    setter(val.Value);
                    _fieldProvenance[fieldName] = platformSource;
                    return;
                }
            }
        }

        // 4. Global priority order
        foreach (var layer in automatedLayers)
        {
            if (layerData.TryGetValue(layer.SourceId, out var payload))
            {
                var val = extractor(payload);
                if (val.HasValue)
                {
                    setter(val.Value);
                    _fieldProvenance[fieldName] = layer.SourceId;
                    return;
                }
            }
        }
    }

    private TitleMetadataLayer GetOrCreateLayer(string sourceId, MetadataSourceType sourceType)
    {
        var existing = GetMetadataLayer(sourceId);
        if (existing != null)
        {
            return existing;
        }

        var newLayer = TitleMetadataLayer.CreateNew(Id, sourceId, sourceType, new TitleMetadataPayload());
        _metadataLayers.Add(newLayer);
        return newLayer;
    }

    private void ResetEffectiveMetadata()
    {
        Description = null;
        Publisher = null;
        Developer = null;
        Genre = null;
        ReleaseDate = null;
        Players = null;
        Rating = null;
        ConservativeMinimumAge = null;
        _contentRatings.Clear();
    }

    #endregion

    #region Merge Operations

    /// <summary>
    ///     Absorbs another title's metadata layers, external IDs, and media.
    ///     Target (this) wins on conflicts; source fills gaps only.
    /// </summary>
    public void Absorb(Title source, IReadOnlyList<string> sourcePriorityOrder)
    {
        // 1. Merge external IDs (source fills gaps only; confirmed target links are never overwritten)
        foreach (var sourceExtId in source.ExternalIds)
        {
            var existing = GetExternalId(sourceExtId.Provider);
            if (existing is null)
            {
                _externalIds.Add(TitleExternalId.CreateNew(
                    Id,
                    sourceExtId.Provider,
                    sourceExtId.ExternalId,
                    sourceExtId.MatchConfidence));
            }
            // If target has a confirmed link for this provider, skip source's link
        }

        // 2. Merge metadata layers (source fills gaps only)
        foreach (var sourceLayer in source.MetadataLayers)
        {
            var existing = GetMetadataLayer(sourceLayer.SourceId);
            if (existing is null)
            {
                var payload = sourceLayer.GetPayload() ?? new TitleMetadataPayload();
                var newLayer = TitleMetadataLayer.CreateNew(
                    Id,
                    sourceLayer.SourceId,
                    sourceLayer.SourceType,
                    payload);
                _metadataLayers.Add(newLayer);
            }
        }

        // 3. Merge media (source fills gaps only, respecting unique constraint)
        foreach (var sourceMedia in source.Media)
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
