namespace Romd.Consumer.Application.Account;

/// <summary>
///     Translates between the registered region and language keys consumers exchange and the internal ids settings
///     store. Unregistered rows are invisible here: their keys never resolve and their ids never map back.
/// </summary>
public interface IConsumerTaxonomyKeys
{
    Task<IReadOnlyDictionary<string, int>> RegionIdsAsync(IReadOnlyCollection<string> keys, CancellationToken ct = default);
    Task<IReadOnlyDictionary<string, int>> LanguageIdsAsync(IReadOnlyCollection<string> keys, CancellationToken ct = default);
    Task<IReadOnlyDictionary<int, string>> RegionKeysAsync(IReadOnlyCollection<int> ids, CancellationToken ct = default);
    Task<IReadOnlyDictionary<int, string>> LanguageKeysAsync(IReadOnlyCollection<int> ids, CancellationToken ct = default);
}
