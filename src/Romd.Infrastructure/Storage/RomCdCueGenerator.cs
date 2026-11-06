using System.Security.Cryptography;
using ErrorOr;
using Moonlark.Libchdr;
using Romd.Admin.Application.Source.Rom.Compressed;
using Romd.Admin.Application.Storage.Files;
using Romd.Domain.Hashing;
using Romd.Domain.Source.Rom;
using Romd.Media.PlayStation;

namespace Romd.Infrastructure.Storage;

/// <summary>Bounded formatter and managed write into the existing dedicated CUE pool; never reacquires the source staging pool.</summary>
public sealed class RomCdCueGenerator(ITempFileFactory files) : IRomCdCueGenerator
{
    public async Task<ErrorOr<GeneratedCdCue>> GenerateAsync(IReadOnlyList<RomCdInspectedTrack> tracks,
        IReadOnlyList<string> trackNames, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(tracks);
        ArgumentNullException.ThrowIfNull(trackNames);
        if (tracks.Count != trackNames.Count || tracks.Any(track => track is null || track.Placement is null || track.Identity is null))
            return RomCdImportErrors.UnsupportedLayout;
        byte[] bytes;
        try
        {
            bytes = ChdCdCueRegenerator.Regenerate(tracks.Select((track, index) => new ChdCdCueTrackFile(trackNames[index],
                new(checked((uint)track.Placement.Number), track.Placement.Kind switch
                {
                    RomCdTrackKind.Mode1Raw => ChdCdTrackType.Mode1Raw,
                    RomCdTrackKind.Mode2Raw => ChdCdTrackType.Mode2Raw,
                    RomCdTrackKind.Audio => ChdCdTrackType.Audio,
                    _ => throw new NotSupportedException("Unsupported raw track mode.")
                }, checked((ulong)track.Placement.FirstFrame), checked((uint)track.Placement.Frames), checked((uint)track.AlignmentFrames),
                checked((uint)track.PregapFrames), track.PregapStored, checked((uint)track.PostgapFrames), track.Identity.Size,
                track.Identity.Sha256.ToString(), track.Identity.Sha1.ToString(), track.Identity.Md5.ToString(), track.Identity.Crc32.ToString()))).ToArray());
        }
        catch (Exception error) when (error is InvalidDataException or ArgumentException or NotSupportedException or OverflowException)
        { return RomCdImportErrors.UnsupportedLayout; }
        if (bytes.Length is < 1 or > 65536)
            return RomCdImportErrors.UnsupportedLayout;
        var identity = new RomLogicalIdentity(bytes.Length, Sha256.FromSpan(SHA256.HashData(bytes)), Sha1.FromSpan(SHA1.HashData(bytes)),
            Md5.FromSpan(MD5.HashData(bytes)), Crc32.FromUInt32(System.IO.Hashing.Crc32.HashToUInt32(bytes)));
        var reserved = await files.ReserveCdCueAsync(bytes.Length, ct);
        if (reserved.IsError)
            return reserved.Errors;
        await using var writable = reserved.Value;
        await using (var output = new FileStream(writable.Path, new FileStreamOptions
        { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None, Options = FileOptions.Asynchronous | FileOptions.SequentialScan }))
            await output.WriteAsync(bytes, ct);
        var sealedFile = await writable.SealAsync(new(identity.Sha256, identity.Md5, identity.Sha1, identity.Size), ct);
        return sealedFile.IsError ? sealedFile.Errors : new GeneratedCdCue(sealedFile.Value, identity, ChdCdCueRegenerator.Profile);
    }
}
