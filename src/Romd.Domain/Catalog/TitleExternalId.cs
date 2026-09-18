namespace Romd.Domain.Catalog;

/// <summary>
///     Represents an external identifier linking a Title to a metadata provider.
///     Allows a single title to be associated with multiple providers (IGDB, ScreenScraper, etc.).
/// </summary>
public sealed class TitleExternalId
{
    private TitleExternalId(
        int id,
        int titleId,
        string provider,
        string externalId,
        float matchConfidence,
        bool isConfirmed,
        DateTimeOffset createdAt)
    {
        Id = id;
        TitleId = titleId;
        Provider = provider;
        ExternalId = externalId;
        MatchConfidence = matchConfidence;
        IsConfirmed = isConfirmed;
        CreatedAt = createdAt;
    }

    public int Id { get; private set; }
    public int TitleId { get; private set; }

    /// <summary>
    ///     Provider identifier (e.g., "igdb", "screenscraper").
    /// </summary>
    public string Provider { get; private set; }

    /// <summary>
    ///     The external system's ID for this title.
    /// </summary>
    public string ExternalId { get; private set; }

    /// <summary>
    ///     Match confidence score from 0.0 to 1.0.
    ///     1.0 indicates an exact match, lower values indicate fuzzy matches.
    /// </summary>
    public float MatchConfidence { get; private set; }

    /// <summary>
    ///     Whether this external ID has been confirmed by a user.
    ///     Confirmed IDs are immutable to auto-enrichment.
    /// </summary>
    public bool IsConfirmed { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    /// <summary>
    ///     Creates a new external ID entry with validated invariants.
    /// </summary>
    /// <exception cref="ArgumentException">Thrown when provider or externalId is null or whitespace.</exception>
    public static TitleExternalId CreateNew(
        int titleId,
        string provider,
        string externalId,
        float matchConfidence)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(provider);
        ArgumentException.ThrowIfNullOrWhiteSpace(externalId);

        return new TitleExternalId(
            id: 0,
            titleId: titleId,
            provider: provider,
            externalId: externalId,
            matchConfidence: matchConfidence,
            isConfirmed: false,
            createdAt: DateTimeOffset.UtcNow);
    }

    /// <summary>
    ///     Rehydrates an external ID entry from persistence. Trusts that data is valid.
    /// </summary>
    internal static TitleExternalId Rehydrate(
        int id,
        int titleId,
        string provider,
        string externalId,
        float matchConfidence,
        bool isConfirmed,
        DateTimeOffset createdAt)
    {
        return new TitleExternalId(id, titleId, provider, externalId, matchConfidence, isConfirmed, createdAt);
    }

    /// <summary>
    ///     Attempts to update this external ID from an auto-enrichment match.
    ///     No-op if this external ID has been confirmed by a user.
    /// </summary>
    /// <returns>True if the update was applied, false if rejected (confirmed).</returns>
    public bool TryUpdateAutoMatch(string externalId, float matchConfidence)
    {
        if (IsConfirmed)
            return false;

        if (matchConfidence < MatchConfidence)
            return false;

        ExternalId = externalId;
        MatchConfidence = matchConfidence;
        return true;
    }

    /// <summary>
    ///     Sets the external ID manually by a user. Always succeeds and marks as confirmed.
    /// </summary>
    public void SetManually(string externalId)
    {
        ExternalId = externalId;
        MatchConfidence = 1.0f;
        IsConfirmed = true;
    }

    /// <summary>
    ///     Confirms this external ID, making it immutable to auto-enrichment.
    /// </summary>
    public void Confirm()
    {
        IsConfirmed = true;
    }
}
