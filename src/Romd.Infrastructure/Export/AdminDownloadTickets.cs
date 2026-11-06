using System.Security.Cryptography;
using Microsoft.Extensions.Caching.Memory;
using Romd.Admin.Application.Export;

namespace Romd.Infrastructure.Export;

/// <summary>Short-lived capabilities for this Admin host only. No bearer token enters a URL.
/// Tickets survive retries until expiry but intentionally do not survive a host restart.</summary>
public sealed class AdminDownloadTickets(TimeProvider time) : IAdminDownloadTickets, IDisposable
{
    private readonly MemoryCache _cache = new(new MemoryCacheOptions { SizeLimit = 100_000 });
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(2);

    public AdminDownloadTicket Issue(AdminDownloadPlan plan)
    {
        bool completed = plan.CompletedExportPath is not null;
        if (completed
            ? !plan.IsArchive || plan.Entries.Count != 0 || !Path.IsPathFullyQualified(plan.CompletedExportPath!)
                || plan.Filename != ExportArchivePaths.SafeFilename(plan.Filename)
            : plan.Entries.Count is 0 or > 10_000)
            throw new ArgumentException("Invalid download plan.", nameof(plan));
        var token = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
        var expires = time.GetUtcNow().Add(Lifetime);
        var snapshot = plan with { Entries = Array.AsReadOnly(plan.Entries.ToArray()) };
        _cache.Set(token, (snapshot, expires), new MemoryCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = Lifetime,
            Size = completed ? 1 : plan.Entries.Count
        });
        return new($"/api/download-tickets/{token}", expires);
    }

    public AdminDownloadPlan? Resolve(string token) => token.Length == 64 &&
        _cache.TryGetValue<(AdminDownloadPlan Plan, DateTimeOffset Expires)>(token, out var ticket) &&
        ticket.Expires > time.GetUtcNow() ? ticket.Plan : null;

    public void Dispose() => _cache.Dispose();
}
