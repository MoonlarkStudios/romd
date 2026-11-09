using Romd.Application.Common.Content;
using Romd.Domain.Hashing;
using Romd.Contracts.Consumer.Releases;

namespace Romd.Consumer.Application.Delivery;

public static class ConsumerDiscSetArchiveFormats
{
    public static IReadOnlyList<ConsumerDiscSetArchiveFormat> Choices { get; } = Array.AsReadOnly<ConsumerDiscSetArchiveFormat>(
        [new("original", "original ZIP"), new("chd", "CHD where available")]);
}

public abstract record ConsumerDiscSetArchiveSource
{
    private ConsumerDiscSetArchiveSource() { }
    public sealed record Original(RomDeliveryTarget Target) : ConsumerDiscSetArchiveSource;
    public sealed record Descriptor : ConsumerDiscSetArchiveSource;
    public sealed record CdArchive(RomCdArchiveDownload Target) : ConsumerDiscSetArchiveSource;
}

public sealed record ConsumerDiscSetArchiveEntry(string Path, Sha256 Sha256, long SizeBytes, ConsumerDiscSetArchiveSource Source);

/// <summary>A bounded, authorized byte selection. Redemption never selects replacement formats or expands membership.</summary>
public sealed class ConsumerDiscSetArchive(string filename, string playlist, IReadOnlyList<ConsumerDiscSetArchiveEntry> entries,
    string playlistPath = "playlist.m3u")
{
    public string Filename { get; } = filename;
    public string Playlist { get; } = playlist;
    public string PlaylistPath { get; } = playlistPath;
    public IReadOnlyList<ConsumerDiscSetArchiveEntry> Entries { get; } = Array.AsReadOnly(entries.ToArray());
}

public interface IConsumerDiscSetDownloadTickets
{
    ConsumerIssuedContentGrant Issue(ConsumerDiscSetArchive archive);
    ConsumerDiscSetArchive? Resolve(string token);
}

public interface IConsumerDiscSetArchiveWriter
{
    Task WriteAsync(Stream destination, ConsumerDiscSetArchive archive, CancellationToken ct = default);
}
