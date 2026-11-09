using Romd.Application.Common.Content;
using Romd.Domain.Hashing;
using Romd.Domain.Source.Rom;
using Romd.Media;
using Romd.Media.Chd;
using Romd.Storage;

namespace Romd.Infrastructure.Storage;

/// <summary>
/// Opens an admitted DVD over its owned synchronous raw CAS snapshot, inside a decoder read slot. The native pool
/// shares the configured cache and caps native handles only; serving authority remains a separate mandatory check.
/// </summary>
public sealed partial class ChdMediaAdapter : IRomMediaAdapter, IAsyncDisposable
{
    private static readonly ChdHeaderPreflightLimits _limits = ChdDvdLimits.Preflight;
    private readonly ChdReadPool _pool;
    private readonly Func<CancellationToken, ChdNativeIdentity> _verifyNative;
    private readonly DeviceOpener _openDevice;
    private int _disposed;

    public ChdMediaAdapter(ChdNativeCandidate native, MediaBlockCache cache, ReadDecoderOptions options)
        : this(cache, options, (native ?? throw new ArgumentNullException(nameof(native))).VerifyAndLoad, OpenDeviceAsync) { }

    internal ChdMediaAdapter(MediaBlockCache cache, ReadDecoderOptions options,
        Func<CancellationToken, ChdNativeIdentity> verifyNative, DeviceOpener openDevice)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(verifyNative);
        ArgumentNullException.ThrowIfNull(openDevice);
        options.Validate();
        // Native handles only; the original decoder lease retains its one admission while device
        // disposal joins its owned demand/speculative work. Serial reads join surviving cancelled work
        // before borrowing another handle, and speculation immediately reserves spare native capacity.
        _pool = new(cache, options.MaxConcurrent, options.MaxIdle);
        _verifyNative = verifyNative;
        _openDevice = openDevice;
    }

    internal ChdReadPoolSnapshot Snapshot => _pool.Snapshot;

    RomRepresentationEncoding IRomMediaAdapter.Encoding => RomRepresentationEncoding.Chd;

    // Integrity, header and native build are rechecked on every open, so an idle image must never stand in for one.
    bool IRomMediaAdapter.ReusesIdleImages => false;

    // The pool already shares the host cache; native callbacks need synchronous raw access, never a compressed fallback.
    ValueTask<IRandomAccessContent> IRomMediaAdapter.OpenAsync(RomArtifactSource source, RomMediaOpenContext context,
        MediaBlockCache cache, CancellationToken ct) => context.Evidence switch
        {
            ChdOpenEvidence.Dvd { Measurement: { } evidence } when context.Artifact is { } artifact =>
                OpenAsync(source.OpenSynchronousRaw(), evidence, artifact, context.AdmissionTimeout, ct),
            ChdOpenEvidence.Cd { Member: { } member } when context.Artifact is { } artifact =>
                OpenCdAsync(source.OpenSynchronousRaw(), member, artifact, context.AdmissionTimeout, ct),
            _ => throw new InvalidDataException("CHD opens require admitted retained evidence for the expected artifact.")
        };

    /// <summary>Takes ownership of source, including failed or cancelled opens.</summary>
    internal async ValueTask<IRandomAccessContent> OpenAsync(ISynchronousRawContent source, RomChdDeliveryEvidence evidence,
        Sha256 artifact, TimeSpan? admissionTimeout = null, CancellationToken ct = default)
    {
        bool transferred = false;
        IRandomAccessContent? device = null;
        try
        {
            ArgumentNullException.ThrowIfNull(source);
            ArgumentNullException.ThrowIfNull(evidence);
            ct.ThrowIfCancellationRequested();
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            if (admissionTimeout is { } wait && (wait < TimeSpan.Zero || wait > TimeSpan.FromMinutes(5)))
                throw new ArgumentOutOfRangeException(nameof(admissionTimeout));
            if (evidence.Facts.ArtifactSha256 != artifact || evidence.Facts.ArtifactSize != source.Length)
                throw new InvalidDataException("CHD raw snapshot does not match its retained evidence.");
            ValidateHeader(source, evidence.Container, _limits, ct);
            if (ChdBuildEvidence.Decoder(_verifyNative(ct)) != evidence.Decoder)
                throw new InvalidDataException("Deployed CHD decoder disagrees with the retained verified build.");
            ct.ThrowIfCancellationRequested();
            transferred = true; // Device opening owns source on every path; no second decoder admission.
            device = await _openDevice(source, _pool, _limits, artifact.ToString(), admissionTimeout, ct);
            ct.ThrowIfCancellationRequested();
            if (device.Length != evidence.Output.Identity.Size)
                throw new InvalidDataException("CHD decoded length disagrees with its retained measurement.");
            return new Content(device);
        }
        catch (TimeoutException)
        {
            if (device is not null)
                await device.DisposeAsync();
            else if (!transferred)
                source?.Dispose();
            throw new ResourceCapacityException("CHD read capacity is occupied; retry later.");
        }
        catch
        {
            if (device is not null)
                await device.DisposeAsync();
            else if (!transferred)
                source?.Dispose();
            throw;
        }
    }

    private static void ValidateHeader(ISynchronousRawContent source, RomContainerFacts.Chd expected, ChdHeaderPreflightLimits limits, CancellationToken ct)
    {
        byte[] header = new byte[124];
        int received = 0;
        while (received < header.Length)
        {
            ct.ThrowIfCancellationRequested();
            int count = source.ReadAt(received, header.AsSpan(received));
            if (count == 0)
                break;
            if (count < 0 || count > header.Length - received)
                throw new InvalidDataException("CHD source returned an invalid header byte count.");
            received += count;
        }
        var result = ChdHeaderPreflight.Inspect(header.AsSpan(0, received), source.Length, limits);
        if (result is not ChdHeaderPreflightResult.Accepted accepted)
            throw new InvalidDataException("CHD raw snapshot has an inadmissible fixed header.");
        var facts = accepted.Facts;
        if (facts.LogicalBytes != (ulong)expected.LogicalBytes || facts.HunkBytes != expected.HunkBytes ||
            facts.UnitBytes != expected.UnitBytes || facts.HunkCount != expected.HunkCount ||
            facts.Codec0 != expected.Codecs[0] || facts.Codec1 != expected.Codecs[1] || facts.Codec2 != expected.Codecs[2] ||
            facts.Codec3 != expected.Codecs[3] || Sha1.FromSpan(header.AsSpan(64, 20)) != expected.StoredRawSha1 ||
            Sha1.FromSpan(header.AsSpan(84, 20)) != expected.StoredOverallSha1)
            throw new InvalidDataException("CHD fixed header disagrees with its retained integrity facts.");
    }

    private static async ValueTask<IRandomAccessContent> OpenDeviceAsync(ISynchronousRawContent source, ChdReadPool pool,
        ChdHeaderPreflightLimits limits, string identity, TimeSpan? timeout, CancellationToken ct) =>
        await ChdBlockDevice.OpenAsync(source, pool, limits, identity, timeout, ct);

    public ValueTask DisposeAsync()
    {
        Interlocked.Exchange(ref _disposed, 1);
        return _pool.DisposeAsync();
    }

    internal delegate ValueTask<IRandomAccessContent> DeviceOpener(ISynchronousRawContent source, ChdReadPool pool,
        ChdHeaderPreflightLimits limits, string identity, TimeSpan? timeout, CancellationToken ct);

    private sealed class Content(IRandomAccessContent device) : IRandomAccessContent
    {
        public long Length => device.Length;
        public ContentReadIntegrity Integrity => device.Integrity;
        public async ValueTask<int> ReadAtAsync(long offset, Memory<byte> destination, CancellationToken ct = default)
        {
            try
            { return await device.ReadAtAsync(offset, destination, ct); }
            catch (TimeoutException) { throw new ResourceCapacityException("CHD read capacity is occupied; retry later."); }
        }
        public ValueTask DisposeAsync() => device.DisposeAsync();
    }
}
