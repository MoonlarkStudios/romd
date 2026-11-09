using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Romd.Application.Common.Content;
using Romd.Consumer.Application.Delivery;
using Romd.Domain.Paths;
using Romd.Infrastructure.Export;

namespace Romd.Infrastructure.Delivery;

public sealed class ConsumerDiscSetArchiveWriter(IRomContentReader roms, IConsumerContentArtifactResolver descriptors,
    IRomCdArchiveDownloadReader archives) : IConsumerDiscSetArchiveWriter
{
    public async Task WriteAsync(Stream destination, ConsumerDiscSetArchive plan, CancellationToken ct = default)
    {
        if (PortablePathSet.Validate(plan.Entries.Select(x => x.Path).Append(plan.PlaylistPath)) is { } problem)
            throw new InvalidOperationException(problem);
        using var output = new ArchiveOutputStream(destination, ct);
        await using (var zip = await ZipArchive.CreateAsync(output, ZipArchiveMode.Create, leaveOpen: true, entryNameEncoding: null, cancellationToken: ct))
        {
            foreach (var entry in plan.Entries)
            {
                ct.ThrowIfCancellationRequested();
                await using var input = entry.Source switch
                {
                    ConsumerDiscSetArchiveSource.Original original => await roms.OpenAsync(original.Target, ct, TimeSpan.FromSeconds(30)),
                    ConsumerDiscSetArchiveSource.Descriptor => await descriptors.ResolveAsync(entry.Sha256, ct),
                    ConsumerDiscSetArchiveSource.CdArchive archive => await archives.OpenAsync(archive.Target, ct),
                    _ => throw new InvalidOperationException("Unknown archive source.")
                };
                if (input is null)
                    throw new FileNotFoundException("A selected disc-set file is no longer available.");
                await using (var target = await zip.CreateEntry(entry.Path, CompressionLevel.NoCompression).OpenAsync(ct))
                {
                    using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                    byte[] buffer = new byte[64 * 1024];
                    long total = 0;
                    int count;
                    while ((count = await input.ReadAsync(buffer, ct)) > 0)
                    {
                        if (count > entry.SizeBytes - total)
                            throw new InvalidDataException("A disc-set file exceeds its selected length.");
                        total += count;
                        hash.AppendData(buffer, 0, count);
                        await target.WriteAsync(buffer.AsMemory(0, count), ct);
                    }
                    if (total != entry.SizeBytes || !hash.GetHashAndReset().AsSpan().SequenceEqual(entry.Sha256.AsSpan()))
                        throw new InvalidDataException("A disc-set file does not match its selected identity.");
                }
                await output.FlushAsync(ct);
            }
            await using var playlist = await zip.CreateEntry(plan.PlaylistPath, CompressionLevel.NoCompression).OpenAsync(ct);
            await playlist.WriteAsync(Encoding.UTF8.GetBytes(plan.Playlist), ct);
        }
        await output.FlushAsync(ct);
    }
}
