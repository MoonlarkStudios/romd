using Microsoft.EntityFrameworkCore;
using Romd.Admin.Application.ConsumerApps;
using Romd.Application.Common.Security;
using Romd.Persistence.Entities;

namespace Romd.Persistence.Identity;

public sealed class ConsumerWebAppRegistrationStore(RomdDbContext db)
    : IConsumerWebAppRegistrations, IConsumerWebAppAdministration
{
    public async Task<ConsumerWebAppRegistration?> FindAsync(string clientId, CancellationToken ct) =>
        (await db.ConsumerWebAppRegistrations.SingleOrDefaultAsync(item => item.ClientId == clientId, ct))?.ToRegistration();

    public Task<bool> IsActiveOriginAsync(string browserOrigin, CancellationToken ct) =>
        db.ConsumerWebAppRegistrations.AnyAsync(item => item.BrowserOrigin == browserOrigin
            && item.ProvisionedAt != null && item.DisabledAt == null, ct);

    public async Task<IReadOnlyList<ConsumerWebAppRegistration>> ListAsync(ConsumerWebAppPosition? after, int limit, CancellationToken ct)
    {
        IQueryable<ConsumerWebAppRegistrationEntity> rows = db.ConsumerWebAppRegistrations;
        if (after is not null)
            rows = rows.Where(item => string.Compare(item.DisplayName, after.DisplayName) > 0 ||
                item.DisplayName == after.DisplayName && string.Compare(item.ClientId, after.ClientId) > 0);

        return (await rows.OrderBy(item => item.DisplayName).ThenBy(item => item.ClientId).Take(limit).ToListAsync(ct))
            .Select(item => item.ToRegistration()).ToArray();
    }

    public void StageRegistration(ConsumerWebAppRegistration registration) =>
        db.ConsumerWebAppRegistrations.Add(new ConsumerWebAppRegistrationEntity
        {
            ClientId = registration.ClientId,
            DisplayName = registration.DisplayName,
            CallbackUri = registration.CallbackUri,
            BrowserOrigin = registration.BrowserOrigin
        });

    // A single conditional update makes disablement irreversible and safe against concurrent calls.
    public async Task<bool> DisableStagedAsync(string clientId, DateTimeOffset disabledAt, CancellationToken ct)
    {
        if (db.Database.CurrentTransaction is null)
            throw new InvalidOperationException("Disabling a consumer web app requires a transaction.");

        var registration = db.ConsumerWebAppRegistrations.Where(item => item.ClientId == clientId);
        return await registration.Where(item => item.DisabledAt == null)
                .ExecuteUpdateAsync(set => set.SetProperty(item => item.DisabledAt, disabledAt), ct) == 1
            || await registration.AnyAsync(ct);
    }

    public async Task<IReadOnlyList<ConsumerWebAppRegistration>> ListPendingAsync(DateTimeOffset now, CancellationToken ct) =>
        (await PendingRegistrations()
            .Where(item => item.RetryProvisioningAt == null || item.RetryProvisioningAt <= now)
            .OrderBy(item => item.RetryProvisioningAt != null).ThenBy(item => item.RetryProvisioningAt)
            .ThenBy(item => item.ClientId).Take(50).ToListAsync(ct))
        .Select(item => item.ToRegistration()).ToArray();

    public Task MarkProvisionedAsync(string clientId, DateTimeOffset provisionedAt, CancellationToken ct) =>
        PendingRegistrations().Where(item => item.ClientId == clientId)
            .ExecuteUpdateAsync(set => set.SetProperty(item => item.ProvisionedAt, provisionedAt)
                .SetProperty(item => item.RetryProvisioningAt, (DateTimeOffset?)null), ct);

    public Task ScheduleProvisioningRetryAsync(string clientId, DateTimeOffset retryAt, CancellationToken ct) =>
        PendingRegistrations().Where(item => item.ClientId == clientId)
            .ExecuteUpdateAsync(set => set.SetProperty(item => item.RetryProvisioningAt, retryAt), ct);

    private IQueryable<ConsumerWebAppRegistrationEntity> PendingRegistrations() =>
        db.ConsumerWebAppRegistrations.Where(item => item.ProvisionedAt == null && item.DisabledAt == null);
}
