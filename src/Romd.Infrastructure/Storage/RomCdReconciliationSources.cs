using ErrorOr;
using Romd.Admin.Application.Source.Rom.Reconciliation;
using Romd.Admin.Application.Storage.Files;
using Romd.Application.Common.Content;
using Romd.Domain.Source.Rom;

namespace Romd.Infrastructure.Storage;

/// <summary>Materializes only the captured original files, including pinned nonpreferred logical outputs.</summary>
internal sealed class RomCdReconciliationSources : IAsyncDisposable
{
    private readonly List<ITempFile> _files = [];
    private int _disposed;
    private RomCdReconciliationSources(IReadOnlyList<MediaReconciliationInput> inputs) => Inputs = inputs;
    private IReadOnlyList<MediaReconciliationInput> Inputs { get; }
    internal KeyValuePair<string, ITempFile> Cue
    {
        get { var cue = Pairs.Single(pair => pair.Input.Role == MediaReconciliationSourceRole.Cue); return new(cue.Input.Path!, cue.File); }
    }
    private IEnumerable<(MediaReconciliationInput Input, ITempFile File)> Pairs => Inputs.Select((input, index) => (input, _files[index]));
    internal IReadOnlyDictionary<string, ITempFile> Bins => Pairs.Where(pair => pair.Input is MediaReconciliationInput.Logical)
        .ToDictionary(pair => pair.Input.Path!, pair => pair.File, StringComparer.Ordinal);
    internal IReadOnlyDictionary<string, ITempFile> Stored => Pairs.Where(pair => pair.Input is MediaReconciliationInput.Stored)
        .ToDictionary(pair => pair.Input.Path!, pair => pair.File, StringComparer.Ordinal);
    internal IReadOnlyDictionary<string, ITempFile> Sidecars => Pairs.Where(pair => pair.Input.Role == MediaReconciliationSourceRole.Sidecar)
        .ToDictionary(pair => pair.Input.Path!, pair => pair.File, StringComparer.Ordinal);

    internal static async Task<ErrorOr<RomCdReconciliationSources>> MaterializeAsync(
        IReadOnlyList<MediaReconciliationInput> inputs, LocalTempFileFactory.CdAttemptStaging staging, int firstOrdinal,
        IFileStorageService files, RomContentReader reader, CancellationToken ct)
    {
        var sources = new RomCdReconciliationSources(inputs);
        try
        {
            for (int ordinal = 0; ordinal < inputs.Count; ordinal++)
            {
                var input = inputs[ordinal];
                RomCdFileIdentity expected = input is MediaReconciliationInput.Logical logical
                    ? new(logical.Output.Identity.Size, logical.Output.Identity.Sha256, logical.Output.Identity.Sha1, logical.Output.Identity.Md5, logical.Output.Identity.Crc32)
                    : ((MediaReconciliationInput.Stored)input).Identity;
                await using var stream = await OpenAsync(input, files, reader, ct);
                if (stream is null)
                { await sources.DisposeAsync(); return MediaReconciliationErrors.Changed; }
                var original = await staging.MaterializeAsync(firstOrdinal + ordinal, stream, expected.Sha256, ct);
                sources._files.Add(original);
                if (original.DatHashes is not { } hashes || hashes.BytesRead != expected.Size || hashes.Sha1 != expected.Sha1 ||
                    hashes.Md5 != expected.Md5 || hashes.Crc32 != expected.Crc32)
                { await sources.DisposeAsync(); return MediaReconciliationErrors.Measurement; }
            }
            return sources;
        }
        catch { await sources.DisposeAsync(); throw; }
    }
    private static async Task<Stream?> OpenAsync(MediaReconciliationInput input, IFileStorageService files, RomContentReader reader, CancellationToken ct)
    {
        if (input is MediaReconciliationInput.Stored stored)
            return await files.RetrieveByIdAsync(stored.FileId, ct);
        var logical = (MediaReconciliationInput.Logical)input;
        var artifact = new RomContentArtifact(logical.Graph, logical.Output.Id, true);
        var content = new RomContentDescriptor(logical.Output.RomId!.Value, logical.Output.Id, logical.Output.Identity, logical.Path!, [artifact], [], []);
        return await reader.OpenLogicalAsync(content, ct);
    }
    internal void Quarantine()
    { foreach (var file in _files) try { file.Quarantine(); } catch (Exception) { } }
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        List<Exception> errors = [];
        foreach (var file in _files)
            try
            { await file.DisposeAsync(); }
            catch (Exception error) { errors.Add(error); }
        if (errors.Count > 0)
            throw new AggregateException("Original CD source cleanup failed; unresolved custody remains retained.", errors);
    }
}
