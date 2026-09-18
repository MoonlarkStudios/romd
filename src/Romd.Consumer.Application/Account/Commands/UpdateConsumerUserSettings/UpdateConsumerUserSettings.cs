using ErrorOr;
using Romd.Application.Common.Cqrs;
using Romd.Application.Common.Ids;
using Romd.Application.Common.Security;
using Romd.Consumer.Application.Browse;
using Romd.Contracts.Consumer.Account;

namespace Romd.Consumer.Application.Account.Commands.UpdateConsumerUserSettings;

public sealed record UpdateConsumerUserSettingsCommand(
    string Theme,
    ConsumerReleasePreferenceDto? ReleasePreference) : ICommand<ConsumerUserSettingsDto>;

public sealed class UpdateConsumerUserSettingsCommandHandler(
    ICurrentUser currentUser,
    IConsumerUserSettingsStore settingsStore)
    : ICommandHandler<UpdateConsumerUserSettingsCommand, ConsumerUserSettingsDto>
{
    private static readonly string[] AllowedThemes = ["system", "light", "dark"];

    public async Task<ErrorOr<ConsumerUserSettingsDto>> HandleAsync(
        UpdateConsumerUserSettingsCommand command,
        CancellationToken ct = default)
    {
        if (!currentUser.IsAuthenticated || currentUser.UserId is not { } userId)
        {
            return ConsumerErrors.CurrentUserRequired();
        }

        if (string.IsNullOrWhiteSpace(command.Theme))
        {
            return ConsumerErrors.InvalidSettings("Theme must be one of: system, light, dark.");
        }

        string theme = command.Theme.Trim().ToLowerInvariant();
        if (!AllowedThemes.Contains(theme, StringComparer.Ordinal))
        {
            return ConsumerErrors.InvalidSettings("Theme must be one of: system, light, dark.");
        }

        var currentSettings = command.ReleasePreference is null
            ? await settingsStore.GetSettingsAsync(userId, ct)
            : null;
        var releasePreferenceResult = command.ReleasePreference is null
            ? currentSettings!.ReleasePreference
            : ParseReleasePreference(command.ReleasePreference);
        if (releasePreferenceResult.IsError)
        {
            return releasePreferenceResult.Errors;
        }

        var settings = await settingsStore.SaveSettingsAsync(
            userId,
            new ConsumerAccountSettings(theme, releasePreferenceResult.Value),
            ct);

        return settings.ToDto();
    }

    private static ErrorOr<ConsumerReleasePreference> ParseReleasePreference(
        ConsumerReleasePreferenceDto dto)
    {
        var preferredRegionIds = DecodeIds(dto.PreferredRegionIds, "region");
        if (preferredRegionIds.IsError)
        {
            return preferredRegionIds.Errors;
        }

        var preferredLanguageIds = DecodeIds(dto.PreferredLanguageIds, "language");
        if (preferredLanguageIds.IsError)
        {
            return preferredLanguageIds.Errors;
        }

        if (!TryParseRevisionPreference(dto.RevisionStrategy, out var revisionPreference))
        {
            return ConsumerErrors.InvalidSettings(
                "Revision strategy must be one of: none, newestFirst, oldestFirst.");
        }

        return new ConsumerReleasePreference(
            preferredRegionIds.Value,
            preferredLanguageIds.Value,
            revisionPreference);
    }

    private static ErrorOr<IReadOnlyList<int>> DecodeIds(
        IReadOnlyList<string> encodedIds,
        string fieldName)
    {
        var ids = new List<int>(encodedIds.Count);
        foreach (string encodedId in encodedIds)
        {
            if (!IdCoder.TryDecode(encodedId, out int id))
            {
                return ConsumerErrors.InvalidSettings($"Preferred {fieldName} ID '{encodedId}' is invalid.");
            }

            ids.Add(id);
        }

        return ids;
    }

    private static bool TryParseRevisionPreference(
        string value,
        out ConsumerRevisionPreference preference)
    {
        preference = value.Trim().ToLowerInvariant() switch
        {
            "none" => ConsumerRevisionPreference.None,
            "newestfirst" => ConsumerRevisionPreference.NewestFirst,
            "oldestfirst" => ConsumerRevisionPreference.OldestFirst,
            _ => (ConsumerRevisionPreference)(-1)
        };

        return preference != (ConsumerRevisionPreference)(-1);
    }
}
