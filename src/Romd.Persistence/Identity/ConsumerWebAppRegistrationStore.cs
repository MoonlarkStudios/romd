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

    public async Task<IReadOnlyList<ConsumerWebAppRegistration>> ListAsync(CancellationToken ct) =>
        (await db.ConsumerWebAppRegistrations.OrderBy(item => item.DisplayName).ThenBy(item => item.ClientId).ToListAsync(ct))
        .Select(item => item.ToRegistration()).ToArray();

    public void StageRegistration(ConsumerWebAppRegistration registration) =>
        db.ConsumerWebAppRegistrations.Add(new ConsumerWebAppRegistrationEntity
        {
            ClientId = registration.ClientId,
            DisplayName = registration.DisplayName,
            CallbackUri = registration.CallbackUri,
            BrowserOrigin = registration.BrowserOrigin
        });

    // A single conditional update makes disablement irreversible and safe against concurrent calls.
    public async Task<bool> DisableAsync(string clientId, DateTimeOffset disabledAt, CancellationToken ct)
    {
        var registration = db.ConsumerWebAppRegistrations.Where(item => item.ClientId == clientId);
        return await registration.Where(item => item.DisabledAt == null)
                .ExecuteUpdateAsync(set => set.SetProperty(item => item.DisabledAt, disabledAt), ct) == 1
            || await registration.AnyAsync(ct);
    }

    public async Task<IReadOnlyList<ConsumerWebAppRegistration>> ListPendingAsync(CancellationToken ct) =>
        (await db.ConsumerWebAppRegistrations.Where(item => item.ProvisionedAt == null && item.DisabledAt == null)
            .OrderBy(item => item.ClientId).Take(50).ToListAsync(ct))
        .Select(item => item.ToRegistration()).ToArray();

    public Task MarkProvisionedAsync(string clientId, DateTimeOffset provisionedAt, CancellationToken ct) =>
        db.ConsumerWebAppRegistrations.Where(item => item.ClientId == clientId && item.ProvisionedAt == null && item.DisabledAt == null)
            .ExecuteUpdateAsync(set => set.SetProperty(item => item.ProvisionedAt, provisionedAt), ct);
}
