namespace Moonlark.Libchdr;

/// <summary>Computed and stored container hashes, without an archival or emulator compatibility claim.</summary>
/// <param name="ComputedRawSha1">The SHA-1 of exactly the decoded logical bytes: computed by <see cref="ChdIntegrity.Verify(ChdFile, CancellationToken)"/>,
/// or supplied by the caller of <see cref="ChdIntegrity.VerifyOverall"/>.</param>
/// <param name="StoredRawSha1">The stored raw hash, or zero when absent.</param>
/// <param name="ComputedOverallSha1">The computed raw-plus-checksummed-metadata hash.</param>
/// <param name="StoredOverallSha1">The stored overall hash, or zero when absent.</param>
public readonly record struct ChdIntegrityResult(ChdSha1 ComputedRawSha1, ChdSha1 StoredRawSha1,
    ChdSha1 ComputedOverallSha1, ChdSha1 StoredOverallSha1)
{
    /// <summary>Whether a stored raw SHA-1 is available.</summary>
    public bool HasStoredRawSha1 => !StoredRawSha1.IsEmpty;
    /// <summary>Whether a stored overall SHA-1 is available.</summary>
    public bool HasStoredOverallSha1 => !StoredOverallSha1.IsEmpty;
    /// <summary>Whether the available stored raw hash matches.</summary>
    public bool RawMatches => HasStoredRawSha1 && ComputedRawSha1 == StoredRawSha1;
    /// <summary>Whether the available stored overall hash matches.</summary>
    public bool OverallMatches => HasStoredOverallSha1 && ComputedOverallSha1 == StoredOverallSha1;
    /// <summary>Whether both stored hashes exist and match. Missing hashes never qualify an image.</summary>
    public bool IsVerified => RawMatches && OverallMatches;
}
