using Romd.Admin.Application.Storage.Files;
using Romd.Domain.Source.Rom;
using Romd.Infrastructure.Storage;
using Romd.Media.Chd;
using Romd.Media.Inspection;

namespace Romd.Infrastructure.Tests.Storage;

// Forward the real port unchanged and report only bounded execution metadata, never source contents or paths.
internal sealed class RecordingMediaWorkerSupervisor(IMediaWorkerSupervisor inner, Action<string> write) : IMediaWorkerSupervisor
{
    public bool IsAvailable => inner.IsAvailable;
    public Task<MediaWorkerResult> HandshakeAsync(CancellationToken ct = default) => Record("handshake", inner.HandshakeAsync(ct));
    public Task<MediaWorkerResult> InspectAsync(ITempFile source, CancellationToken ct = default) => Record("inspect", inner.InspectAsync(source, ct));
    public Task<MediaWorkerResult> InspectIsoAsync(ITempFile source, CancellationToken ct = default) => Record("inspect-iso", inner.InspectIsoAsync(source, ct));
    public Task<MediaWorkerResult> InspectChdAsync(ITempFile source, CancellationToken ct = default) => Record("inspect-chd", inner.InspectChdAsync(source, ct));
    public Task<MediaWorkerResult> InspectChdCdAsync(ITempFile source, CancellationToken ct = default) => Record("inspect-cd", inner.InspectChdCdAsync(source, ct));
    public Task<MediaWorkerResult> EncodeChdCdAsync(ITempFile request, IReadOnlyList<ITempFile> files, IWritableTempFile output, ChdmanCandidate candidate, CancellationToken ct = default)
        => Record("encode-cd", inner.EncodeChdCdAsync(request, files, output, candidate, ct));
    public Task<MediaWorkerResult> VerifyChdCdAsync(ITempFile artifact, ITempFile request, IReadOnlyList<ITempFile> files, CancellationToken ct = default)
        => Record("verify-cd", inner.VerifyChdCdAsync(artifact, request, files, ct));
    public Task<MediaWorkerResult> ReverifyChdCdAsync(ITempFile artifact, ITempFile request, IReadOnlyList<ITempFile> files, CancellationToken ct = default)
        => Record("reverify-cd", inner.ReverifyChdCdAsync(artifact, request, files, ct));
    public Task<MediaWorkerResult> EncodeChdAsync(ITempFile source, IWritableTempFile output, ChdmanCandidate candidate, CancellationToken ct = default)
        => Record("encode-dvd", inner.EncodeChdAsync(source, output, candidate, ct));
    public Task<MediaWorkerResult> ExtractChdDvdAsync(ITempFile source, IWritableTempFile output, ChdmanCandidate candidate, long logicalBytes, CancellationToken ct = default)
        => Record("extract-dvd", inner.ExtractChdDvdAsync(source, output, candidate, logicalBytes, ct));
    public Task<MediaWorkerResult> EncodeAsync(ITempFile source, IWritableTempFile output, long logicalBytes, RomIsoDiscKind discKind, CancellationToken ct = default)
        => Record("encode-rvz", inner.EncodeAsync(source, output, logicalBytes, discKind, ct));
    private async Task<MediaWorkerResult> Record(string operation, Task<MediaWorkerResult> pending)
    {
        var result = await pending;
        try
        {
            write($"{operation}: failure={result.Failure}, stopped={result.ProcessTreeStopped}, execution={result.Execution}, " +
                $"reply={result.Reply?.Status}, dvd={result.Reply?.ChdEncodingRejection?.Stage}/{result.Reply?.ChdEncodingRejection?.Cause}, " +
                $"cd={result.Reply?.CdFailure?.Cause}/{result.Reply?.CdFailure?.ToolCause}, tool={result.Reply?.ToolExecution}");
        }
        catch (Exception) { } // Reporting cannot change a real result or its custody handling.
        return result;
    }
}

