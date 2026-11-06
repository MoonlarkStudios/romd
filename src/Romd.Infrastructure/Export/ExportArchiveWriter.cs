using Romd.Application.Common.Content;
using System.IO.Compression;
using Romd.Admin.Application.Export;
using Romd.Admin.Application.Storage.Files;

namespace Romd.Infrastructure.Export;

public sealed class ExportArchiveWriter(IFileStorageService storage, IRomContentReader roms) : IExportArchiveWriter
{
    public async Task WriteAsync(Stream destination, IReadOnlyList<ExportArchiveEntry> entries,
        Func<ExportArchiveEntry, bool, Task>? onEntry = null, bool skipMissing = false,
        CancellationToken ct = default)
    {
        ExportArchivePaths.Validate(entries);
        using var outputStream = new ArchiveOutputStream(destination, ct);
        await using (var archive = await ZipArchive.CreateAsync(outputStream, ZipArchiveMode.Create, leaveOpen: true, entryNameEncoding: null, cancellationToken: ct))
        {
            foreach (var entry in entries)
            {
                ct.ThrowIfCancellationRequested();
                await using var source = entry.Target is { } target
                    ? await roms.OpenAsync(target, ct, TimeSpan.FromSeconds(30))
                    : entry.RomId is null
                    ? await storage.RetrieveByIdAsync(entry.FileId, ct)
                    : await roms.OpenVerifiedByRomIdAsync(entry.RomId.Value, ct, TimeSpan.FromSeconds(30));
                if (source is null && !skipMissing)
                    throw new FileNotFoundException("An export file is no longer available.");
                if (source is not null)
                {
                    var zipEntry = archive.CreateEntry(ExportArchivePaths.Normalize(entry.Path), CompressionLevel.NoCompression);
                    await using var output = await zipEntry.OpenAsync(ct);
                    await source.CopyToAsync(output, ct);
                }
                await outputStream.FlushAsync(ct);
                if (onEntry is not null)
                    await onEntry(entry, source is not null);
            }
        }
        await outputStream.FlushAsync(ct);
    }
}
