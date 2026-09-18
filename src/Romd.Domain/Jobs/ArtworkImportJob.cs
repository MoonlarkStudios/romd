using Romd.Domain.Catalog;

namespace Romd.Domain.Jobs;

/// <summary>
/// Imports one explicitly selected provider asset. Id is the accepted request's
/// durable identity. The original asset URL comes only from a validated server-issued
/// candidate and is revalidated by the provider adapter before every download.
/// </summary>
public sealed class ArtworkImportJob : Job
{
    private ArtworkImportJob() { }

    public int TitleId { get; private set; }
    public ArtworkRole Role { get; private set; }
    public long SelectionRevision { get; private set; }
    public string ProviderId { get; private set; } = null!;
    public string ProviderGameId { get; private set; } = null!;
    public string ProviderAssetId { get; private set; } = null!;
    public string TrustedAssetUrl { get; private set; } = null!;
    public string? Attribution { get; private set; }
    public int FocalX { get; private set; } = 50;
    public int FocalY { get; private set; } = 50;
    public int? RetainedAssetId { get; private set; }
    public bool WasSuperseded { get; private set; }
    public ArtworkImportPhase PhaseEnum { get; private set; }
    public override string Phase => PhaseEnum.ToString();
    public override bool IsTerminal => PhaseEnum is ArtworkImportPhase.Completed
        or ArtworkImportPhase.Failed or ArtworkImportPhase.Cancelled;
    public override double ProgressPercent => PhaseEnum switch
    {
        ArtworkImportPhase.Pending => 0,
        ArtworkImportPhase.Importing => 50,
        _ => 100
    };

    public static ArtworkImportJob Create(Guid requestId, string titleName, int titleId,
        int? platformId, ArtworkRole role, long selectionRevision, string providerId, string providerGameId,
        string providerAssetId, string trustedAssetUrl, string? attribution, TimeProvider timeProvider, Guid? createdByUserId = null,
        int focalX = 50, int focalY = 50)
    {
        if (requestId == Guid.Empty) throw new ArgumentException("A request identity is required.", nameof(requestId));
        ArgumentException.ThrowIfNullOrWhiteSpace(titleName);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(titleId);
        if (platformId is <= 0) throw new ArgumentOutOfRangeException(nameof(platformId));
        if (!Enum.IsDefined(role)) throw new ArgumentOutOfRangeException(nameof(role));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(selectionRevision);
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);
        ArgumentException.ThrowIfNullOrWhiteSpace(providerGameId);
        ArgumentException.ThrowIfNullOrWhiteSpace(providerAssetId);
        if (providerId.Length > 50) throw new ArgumentOutOfRangeException(nameof(providerId));
        if (providerGameId.Length > 100) throw new ArgumentOutOfRangeException(nameof(providerGameId));
        if (providerAssetId.Length > 100) throw new ArgumentOutOfRangeException(nameof(providerAssetId));
        ArgumentException.ThrowIfNullOrWhiteSpace(trustedAssetUrl);
        if (trustedAssetUrl.Length > 2048) throw new ArgumentOutOfRangeException(nameof(trustedAssetUrl));
        if (attribution?.Length > 500) throw new ArgumentOutOfRangeException(nameof(attribution));
        ArgumentNullException.ThrowIfNull(timeProvider);
        if (focalX is < 0 or > 100 || focalY is < 0 or > 100) throw new ArgumentOutOfRangeException(nameof(focalX));
        return new ArtworkImportJob
        {
            Id = requestId,
            CorrelationId = Guid.NewGuid(),
            SourceFilename = titleName,
            TitleId = titleId,
            PlatformId = platformId,
            Role = role,
            SelectionRevision = selectionRevision,
            ProviderId = providerId,
            ProviderGameId = providerGameId,
            ProviderAssetId = providerAssetId,
            TrustedAssetUrl = trustedAssetUrl,
            Attribution = attribution,
            FocalX = focalX, FocalY = focalY,
            PhaseEnum = ArtworkImportPhase.Pending,
            CreatedAt = timeProvider.GetUtcNow(),
            CreatedByUserId = createdByUserId
        };
    }

    /// <summary>
    /// Records the publication result without completing the job. A superseded
    /// request may have been rejected before download, leaving no retained asset.
    /// </summary>
    public void SetImportOutcome(int? retainedAssetId, bool wasSuperseded)
    {
        EnsurePhase(ArtworkImportPhase.Importing);
        if (retainedAssetId is <= 0) throw new ArgumentOutOfRangeException(nameof(retainedAssetId));
        if (retainedAssetId is null && !wasSuperseded)
            throw new ArgumentException("A usable retained asset or a superseded request is required.", nameof(retainedAssetId));
        RetainedAssetId = retainedAssetId;
        WasSuperseded = wasSuperseded;
    }

    internal static ArtworkImportJob Rehydrate(
        Guid id,
        Guid correlationId,
        string sourceFilename,
        int? platformId,
        ArtworkImportPhase phase,
        string? hangfireJobId,
        int titleId,
        ArtworkRole role,
        long selectionRevision,
        string providerId,
        string providerGameId,
        string providerAssetId,
        string trustedAssetUrl,
        string? attribution,
        int? retainedAssetId,
        bool wasSuperseded,
        string? currentItem,
        IReadOnlyList<JobError> errors,
        DateTimeOffset createdAt,
        DateTimeOffset? startedAt,
        DateTimeOffset? completedAt,
        bool isArchived,
        DateTimeOffset? archivedAt,
        Guid? createdByUserId, int focalX = 50, int focalY = 50)
    {
        var job = new ArtworkImportJob
        {
            Id = id, CorrelationId = correlationId, SourceFilename = sourceFilename,
            PlatformId = platformId, PhaseEnum = phase, HangfireJobId = hangfireJobId,
            TitleId = titleId, Role = role, SelectionRevision = selectionRevision, ProviderId = providerId,
            ProviderGameId = providerGameId, ProviderAssetId = providerAssetId,
            TrustedAssetUrl = trustedAssetUrl, Attribution = attribution, RetainedAssetId = retainedAssetId, WasSuperseded = wasSuperseded,
            CurrentItem = currentItem, CreatedAt = createdAt, StartedAt = startedAt,
            CompletedAt = completedAt, IsArchived = isArchived, ArchivedAt = archivedAt,
            CreatedByUserId = createdByUserId, FocalX = focalX, FocalY = focalY
        };
        job._errors.AddRange(errors);
        return job;
    }

    protected override void TransitionFromPending()
    {
        EnsurePhase(ArtworkImportPhase.Pending);
        PhaseEnum = ArtworkImportPhase.Importing;
    }

    protected override void ValidateCanComplete()
    {
        EnsurePhase(ArtworkImportPhase.Importing);
        if (RetainedAssetId is null && !WasSuperseded)
            throw new InvalidOperationException("Import outcome must be recorded before completing artwork import.");
    }

    protected override void SetTerminalPhase(bool hasErrors) => PhaseEnum = ArtworkImportPhase.Completed;
    protected override void SetFailedPhase() => PhaseEnum = ArtworkImportPhase.Failed;
    protected override void SetCancelledPhase() => PhaseEnum = ArtworkImportPhase.Cancelled;

    private void EnsurePhase(ArtworkImportPhase expected)
    {
        if (PhaseEnum != expected)
            throw new InvalidOperationException($"Invalid phase transition: expected {expected}, was {PhaseEnum}");
    }
}
