using ErrorOr;
using Romd.Application.Common.Cqrs;
using Romd.Application.Common.Security;
using Romd.Consumer.Application.Browse;
using Romd.Contracts.Consumer.Account;

namespace Romd.Consumer.Application.Account.Commands.UpdateConsumerUserSettings;

public sealed record UpdateConsumerUserSettingsCommand(
    string? Theme,
    ConsumerReleasePreferenceDto? ReleasePreference) : ICommand<ConsumerUserSettingsDto>;

public sealed class UpdateConsumerUserSettingsCommandHandler(
    ICurrentUser currentUser,
    IConsumerUserSettingsStore settingsStore,
    IConsumerTaxonomyKeys taxonomyKeys)
    : ICommandHandler<UpdateConsumerUserSettingsCommand, ConsumerUserSettingsDto>
{
    public const string ThemeField = "theme";
    public const string RevisionStrategyField = "releasePreference.revisionStrategy";
    public const string PreferredRegionKeysField = "releasePreference.preferredRegionKeys";
    public const string PreferredLanguageKeysField = "releasePreference.preferredLanguageKeys";

    private static readonly string[] AllowedThemes = ["system", "light", "dark"];

    public async Task<ErrorOr<ConsumerUserSettingsDto>> HandleAsync(
        UpdateConsumerUserSettingsCommand command,
        CancellationToken ct = default)
    {
        if (!currentUser.IsAuthenticated || currentUser.UserId is not { } userId)
        {
            return ConsumerErrors.CurrentUserRequired();
        }

        // One validation stage: every failure in it is reported together
        // (docs/decisions/api-contract-policy.md, "One Error Envelope").
        var current = await settingsStore.GetSettingsAsync(userId, ct);
        string? theme = command.Theme is null ? current.Theme : ParseTheme(command.Theme);
        var requested = command.ReleasePreference is null
            ? (ErrorOr<ConsumerReleasePreference>?)null
            : await ParseReleasePreferenceAsync(command.ReleasePreference, ct);
        Error[] themeErrors = theme is null
            ? [ConsumerErrors.InvalidSettings("Theme must be one of: system, light, dark.", ThemeField)]
            : [];
        List<Error> errors = [.. themeErrors, .. requested?.ErrorsOrEmptyList ?? []];
        if (theme is null || errors.Count > 0)
        {
            return errors;
        }

        var releasePreference = requested?.Value ?? current.ReleasePreference;
        var settings = new ConsumerAccountSettings(theme, releasePreference);
        var response = await settings.ToDtoAsync(taxonomyKeys, ct);
        await settingsStore.SaveSettingsAsync(
            userId,
            settings,
            ct);

        return response;
    }

    private static string? ParseTheme(string? theme)
    {
        string? normalized = theme?.Trim().ToLowerInvariant();
        return normalized is not null && AllowedThemes.Contains(normalized, StringComparer.Ordinal) ? normalized : null;
    }

    private async Task<ErrorOr<ConsumerReleasePreference>> ParseReleasePreferenceAsync(
        ConsumerReleasePreferenceDto dto,
        CancellationToken ct)
    {
        Error[] strategyErrors = TryParseRevisionPreference(dto.RevisionStrategy, out var revisionPreference)
            ? []
            : [ConsumerErrors.InvalidSettings("Revision strategy must be one of: none, newestFirst, oldestFirst.", RevisionStrategyField)];
        var regionIds = await ResolveKeysAsync(
            dto.PreferredRegionKeys, taxonomyKeys.RegionIdsAsync, "region", PreferredRegionKeysField, ct);
        var languageIds = await ResolveKeysAsync(
            dto.PreferredLanguageKeys, taxonomyKeys.LanguageIdsAsync, "language", PreferredLanguageKeysField, ct);
        List<Error> errors = [.. strategyErrors, .. regionIds.ErrorsOrEmptyList, .. languageIds.ErrorsOrEmptyList];
        if (errors.Count > 0)
        {
            return errors;
        }

        return new ConsumerReleasePreference(regionIds.Value, languageIds.Value, revisionPreference);
    }

    // Each list is resolved on its own, so one list's failure never hides the other's.
    private static async Task<ErrorOr<List<int>>> ResolveKeysAsync(
        IReadOnlyList<string?>? keys,
        Func<IReadOnlyCollection<string>, CancellationToken, Task<IReadOnlyDictionary<string, int>>> resolve,
        string noun,
        string field,
        CancellationToken ct)
    {
        // The wire contract marks both lists required; a null or missing list is invalid, never "no preference".
        if (keys is null)
        {
            return ConsumerErrors.InvalidSettings("Provide the list of keys; send an empty list for none.", field);
        }

        // The published bound and uniqueness (maxItems, uniqueItems) hold exactly as enforced here.
        if (keys.Count > ConsumerReleasePreferenceDto.MaximumPreferredKeys)
        {
            return ConsumerErrors.InvalidSettings(
                $"Send at most {ConsumerReleasePreferenceDto.MaximumPreferredKeys} keys.", field);
        }

        var resolved = await resolve(Distinct(keys), ct);
        var invalid = Repeated(keys, field).Concat(Unknown(keys, resolved, noun, field)).ToList();
        return invalid.Count > 0 ? invalid : keys.Select(key => resolved[key!]).ToList();
    }

    // A key repeated later in its list is reported at each later position, compared ordinally as uniqueItems is.
    private static IEnumerable<Error> Repeated(IReadOnlyList<string?> keys, string field) =>
        keys.Select((key, index) => (Key: key, Index: index))
            .Where(entry => entry.Key is not null && keys.Take(entry.Index).Contains(entry.Key, StringComparer.Ordinal))
            .Select(entry => ConsumerErrors.InvalidSettings($"Entry {entry.Index} repeats an earlier key.", field));

    private static string[] Distinct(IReadOnlyList<string?> keys) =>
        keys.OfType<string>().Distinct(StringComparer.Ordinal).ToArray();

    // Every unknown or unregistered key is reported by its position; messages never echo the key.
    private static IEnumerable<Error> Unknown(
        IReadOnlyList<string?> keys,
        IReadOnlyDictionary<string, int> resolved,
        string noun,
        string field) =>
        keys.Select((key, index) => (Key: key, Index: index))
            .Where(entry => entry.Key is null || !resolved.ContainsKey(entry.Key))
            .Select(entry => ConsumerErrors.InvalidSettings($"Entry {entry.Index} is not a registered {noun} key.", field));

    private static bool TryParseRevisionPreference(
        string? value,
        out ConsumerRevisionPreference preference)
    {
        preference = value?.Trim().ToLowerInvariant() switch
        {
            "none" => ConsumerRevisionPreference.None,
            "newestfirst" => ConsumerRevisionPreference.NewestFirst,
            "oldestfirst" => ConsumerRevisionPreference.OldestFirst,
            _ => (ConsumerRevisionPreference)(-1)
        };

        return preference != (ConsumerRevisionPreference)(-1);
    }
}
