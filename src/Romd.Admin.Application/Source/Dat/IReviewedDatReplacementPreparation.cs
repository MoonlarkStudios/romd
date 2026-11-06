using ErrorOr;

namespace Romd.Admin.Application.Source.Dat;

public sealed record DatReplacementFacts(
    int ExistingDatId, int SourceId, int ActiveFileId, int? PlatformId,
    string Name, string ActiveSha256, string CandidateSha256);

/// <summary>Owns immutable reviewed bytes without exposing their backing buffer.</summary>
public interface IPreparedDatReplacement : IDisposable
{
    DatReplacementFacts Facts { get; }
    Stream OpenRead();
}

public interface IReviewedDatReplacementPreparation
{
    /// <summary>Reads and validates the exact pinned pair; it never creates a workspace or job.</summary>
    Task<ErrorOr<IPreparedDatReplacement>> PrepareAsync(
        int datId, Stream candidate, string activeHash, string candidateHash, CancellationToken ct = default);
}
