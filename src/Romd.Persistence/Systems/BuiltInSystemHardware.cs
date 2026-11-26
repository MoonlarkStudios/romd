using System.Text.Json;
using Romd.Domain.ReferenceData;

namespace Romd.Persistence.Systems;

/// <summary>Bundled hardware facts keyed by the stable ROMD system identity.</summary>
internal static class BuiltInSystemHardware
{
    private static readonly Lazy<Dictionary<string, SystemHardware>> Facts = new(Load);

    internal static SystemHardware? Get(string key) => Facts.Value.GetValueOrDefault(key);

    internal static IEnumerable<(string Key, int Year)> ReleaseYears() =>
        Facts.Value.Where(fact => fact.Value.ReleaseYear.HasValue)
            .Select(fact => (fact.Key, fact.Value.ReleaseYear!.Value));

    private static Dictionary<string, SystemHardware> Load()
    {
        using var stream = typeof(BuiltInSystemHardware).Assembly.GetManifestResourceStream("Romd.SystemHardware.json")
            ?? throw new InvalidDataException("Bundled system hardware metadata is missing.");
        var facts = JsonSerializer.Deserialize<Dictionary<string, SystemHardware>>(stream, JsonSerializerOptions.Web);
        if (facts is not { Count: > 0 } || facts.Values.Any(hardware => hardware is not { IsValid: true, Category: not null }))
            throw new InvalidDataException("Bundled system hardware metadata is invalid.");
        return facts;
    }
}
