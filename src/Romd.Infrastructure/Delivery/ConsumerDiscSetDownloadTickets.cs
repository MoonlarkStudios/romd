using System.Security.Cryptography;
using Microsoft.Extensions.Caching.Memory;
using Romd.Consumer.Application.Delivery;
using Romd.Domain.Paths;

namespace Romd.Infrastructure.Delivery;

/// <summary>Consumer-host-only, bounded two-minute capabilities. No application token enters a download URL.</summary>
public sealed class ConsumerDiscSetDownloadTickets(TimeProvider time) : IConsumerDiscSetDownloadTickets, IDisposable
{
    private readonly MemoryCache _cache = new(new MemoryCacheOptions { SizeLimit = 100_000 });
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(2);

    public ConsumerIssuedContentGrant Issue(ConsumerDiscSetArchive archive)
    {
        if (archive.Entries.Count is < 1 or >= PortablePathSet.MaximumFiles ||
            PortablePathSet.Validate(archive.Entries.Select(x => x.Path).Append(archive.PlaylistPath)) is not null)
            throw new ArgumentException("Invalid disc-set archive.", nameof(archive));
        string token = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
        var expires = time.GetUtcNow().Add(Lifetime);
        _cache.Set(token, (archive, expires), new MemoryCacheEntryOptions
        { AbsoluteExpirationRelativeToNow = Lifetime, Size = archive.Entries.Count + 1 });
        return new("/delivery/disc-set/" + token, expires);
    }

    public ConsumerDiscSetArchive? Resolve(string token) => token.Length == 64 &&
        _cache.TryGetValue<(ConsumerDiscSetArchive Archive, DateTimeOffset Expires)>(token, out var ticket) &&
        ticket.Expires > time.GetUtcNow() ? ticket.Archive : null;

    public void Dispose() => _cache.Dispose();
}
