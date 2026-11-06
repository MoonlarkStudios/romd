using Romd.Application.Common.Content;
using Romd.Domain.Hashing;
using Romd.Storage;

namespace Romd.Infrastructure.Storage;

/// <summary>
/// One opened artifact snapshot handed to a reader adapter, which claims the access its container needs. A snapshot
/// holds exactly one access kind and never converts, so a synchronous-raw format cannot fall back to compressed
/// random access. A claim moves ownership to the claimant; disposal releases an unclaimed snapshot.
/// </summary>
public sealed class RomArtifactSource : IAsyncDisposable
{
    private IRandomAccessContent? _randomAccess;
    private ISynchronousRawContent? _synchronousRaw;

    private RomArtifactSource(IRandomAccessContent? randomAccess, ISynchronousRawContent? synchronousRaw) =>
        (_randomAccess, _synchronousRaw) = (randomAccess, synchronousRaw);

    public static RomArtifactSource FromRandomAccess(IRandomAccessContent content) =>
        new(content ?? throw new ArgumentNullException(nameof(content)), null);

    public static RomArtifactSource FromSynchronousRaw(ISynchronousRawContent content) =>
        new(null, content ?? throw new ArgumentNullException(nameof(content)));

    public ValueTask<IRandomAccessContent> OpenRandomAccessAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return ValueTask.FromResult(Interlocked.Exchange(ref _randomAccess, null) ??
            throw new NotSupportedException("This artifact snapshot offers no unclaimed random access."));
    }

    public ISynchronousRawContent OpenSynchronousRaw() => Interlocked.Exchange(ref _synchronousRaw, null) ??
        throw new NotSupportedException("This artifact snapshot offers no unclaimed synchronous raw access.");

    public async ValueTask DisposeAsync()
    {
        Interlocked.Exchange(ref _synchronousRaw, null)?.Dispose();
        if (Interlocked.Exchange(ref _randomAccess, null) is { } randomAccess)
            await randomAccess.DisposeAsync();
    }
}

/// <summary>What the reader admitted before opening; adapters recheck the facts their container binds on every open.</summary>
/// <remarks>Only Infrastructure creates contexts, so admitted CHD evidence cannot be supplied from outside delivery
/// admission; the properties are get-only so a <c>with</c> expression cannot rewrite them either.</remarks>
public sealed record RomMediaOpenContext
{
    internal RomMediaOpenContext(
        Sha256? artifact = null, RomChdDeliveryEvidence? chdEvidence = null, TimeSpan? admissionTimeout = null)
    {
        Artifact = artifact;
        Evidence = chdEvidence is null ? null : new ChdOpenEvidence.Dvd(chdEvidence);
        AdmissionTimeout = admissionTimeout;
    }

    /// <summary>Expected artifact SHA-256 and shared block-cache identity; null isolates the open.</summary>
    public Sha256? Artifact { get; }

    /// <summary>Retained CHD evidence the reader admitted for this exact artifact; null otherwise.</summary>
    public RomChdDeliveryEvidence? ChdEvidence => (Evidence as ChdOpenEvidence.Dvd)?.Measurement;
    internal ChdOpenEvidence? Evidence { get; }

    private RomMediaOpenContext(Sha256 artifact, RomReconstruction.CdMember member, TimeSpan? timeout)
    { Artifact = artifact; Evidence = new ChdOpenEvidence.Cd(member); AdmissionTimeout = timeout; }
    internal static RomMediaOpenContext ForCd(Sha256 artifact, RomReconstruction.CdMember member, TimeSpan? timeout) => new(artifact, member, timeout);

    /// <summary>Caller override for bounded read-capacity admission; null uses the configured default and zero rejects immediately.</summary>
    public TimeSpan? AdmissionTimeout { get; }
}

/// <summary>Exactly one admitted format proof; CD member recipes cannot be treated as singleton DVD hashes.</summary>
internal abstract record ChdOpenEvidence
{
    private ChdOpenEvidence() { }
    internal sealed record Dvd(RomChdDeliveryEvidence Measurement) : ChdOpenEvidence;
    internal sealed record Cd(RomReconstruction.CdMember Member) : ChdOpenEvidence;
}
