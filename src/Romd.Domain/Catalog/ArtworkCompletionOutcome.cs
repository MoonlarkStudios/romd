namespace Romd.Domain.Catalog;

public enum ArtworkCompletionOutcome
{
    Updated, AlreadyPresent, NeedsMatch, NeedsReview, Failed, Unavailable, Disabled,
    Unlinked, ProviderUnconfigured, ProviderDisabled, LeftEmpty, Superseded, Untracked, Removed
}
