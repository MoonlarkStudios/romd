namespace Romd.Consumer.Application.Account;

public interface IConsumerUserSettingsStore
{
    Task<ConsumerAccountSettings> GetSettingsAsync(
        Guid userId,
        CancellationToken ct = default);

    Task<ConsumerAccountSettings> SaveSettingsAsync(
        Guid userId,
        ConsumerAccountSettings settings,
        CancellationToken ct = default);
}
