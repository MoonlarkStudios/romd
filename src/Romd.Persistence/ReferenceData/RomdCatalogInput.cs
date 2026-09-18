using System.Text.Json;

namespace Romd.Persistence.ReferenceData;

internal sealed record CompanyInput(string Name, string? Description = null, bool Retired = false);
internal sealed record SystemInput(string Name, string CompactLabel, string[] ManufacturerIds, string[] Aliases,
    Dictionary<string, string> ProviderMappings, string? Description = null, string? IconPath = null, bool Monochrome = false, bool Retired = false);
internal sealed record RegionInput(string Name, int SortOrder, string[] Aliases, string? Description = null, bool Retired = false);
internal sealed record LanguageInput(string Name, int SortOrder, string[] Aliases, string? Description = null, bool Retired = false);
internal sealed record RatingBoardInput(string Key, string Label, string? Description = null, bool Retired = false);
internal sealed record RatingInput(string Board, string Code, string Name, string? Description = null,
    string? IconPath = null, string? Designation = null, int? MinimumAge = null, bool Monochrome = false, bool Retired = false);
internal sealed record RatingsInput(RatingBoardInput[] Boards, RatingInput[] Ratings);
internal sealed record RomdCatalogInput(int SchemaVersion, int CatalogVersion, Dictionary<string, SystemInput> Systems,
    Dictionary<string, CompanyInput> Companies, Dictionary<string, RegionInput> Regions,
    Dictionary<string, LanguageInput> Languages, RatingsInput Ratings)
{
    internal static RomdCatalogInput Parse(JsonElement document)
    {
        var input = document.Deserialize<RomdCatalogInput>(new JsonSerializerOptions(JsonSerializerDefaults.Web)
        { RespectRequiredConstructorParameters = true }) ?? throw new InvalidDataException("Missing bundled catalog.");
        input.Validate();
        return input;
    }
    private void Validate()
    {
        if (SchemaVersion != 1 || CatalogVersion < 1 || Systems is null || Companies is null || Regions is null || Languages is null || Ratings?.Boards is null || Ratings.Ratings is null)
            throw new InvalidDataException("Invalid reference catalog version or missing resource collection.");
        static void Key(string key, int max = 64)
        {
            if (!ReferenceEditing.Label(key, max) || key.StartsWith("local-", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Invalid built-in key: " + key);
        }
        static void Facts(string name, string? description, int max = 200)
        {
            if (!ReferenceEditing.Label(name, max) || description?.Length > 2000) throw new InvalidDataException("Invalid reference facts: " + name);
        }
        foreach (var (key, company) in Companies) { Key(key); Facts((company ?? throw new InvalidDataException("Missing company facts.")).Name, company.Description); }
        foreach (var (key, system) in Systems)
        {
            if (system is null) throw new InvalidDataException("Missing system facts.");
            Key(key, 50); Facts(system.Name, system.Description);
            if (!ReferenceEditing.Label(system.CompactLabel, 40) || system.ManufacturerIds is null || system.Aliases is null || system.ProviderMappings is null || system.ManufacturerIds.Length > 16 || system.ManufacturerIds.Distinct().Count() != system.ManufacturerIds.Length || system.ManufacturerIds.Any(x => !Companies.ContainsKey(x)))
                throw new InvalidDataException("Invalid system relationships or label: " + key);
            if (system.ProviderMappings.Keys.Select(x => x.ToLowerInvariant()).Distinct().Count() != system.ProviderMappings.Count || system.Aliases.Any(x => !ReferenceEditing.Label(x, 200)) || system.ProviderMappings.Any(x => !ReferenceEditing.Label(x.Key, 50) || !ReferenceEditing.Label(x.Value, 200))) throw new InvalidDataException("Invalid system aliases: " + key);
        }
        foreach (var (key, region) in Regions) { Key(key); Facts((region ?? throw new InvalidDataException("Missing region facts.")).Name, region.Description, 100); if (region.SortOrder < 0 || region.Aliases is null || region.Aliases.Any(x => !ReferenceEditing.Label(x, 100))) throw new InvalidDataException("Invalid region: " + key); }
        foreach (var (key, language) in Languages) { Key(key, 10); Facts((language ?? throw new InvalidDataException("Missing language facts.")).Name, language.Description, 100); if (language.SortOrder < 0 || language.Aliases is null || language.Aliases.Any(x => !ReferenceEditing.Label(x, 100))) throw new InvalidDataException("Invalid language: " + key); }
        if (Ratings.Boards.Select(x => (x ?? throw new InvalidDataException("Missing rating board facts.")).Key).Distinct().Count() != Ratings.Boards.Length || Ratings.Ratings.Select(x => ((x ?? throw new InvalidDataException("Missing rating facts.")).Board, x.Code)).Distinct().Count() != Ratings.Ratings.Length) throw new InvalidDataException("Duplicate rating identity.");
        foreach (var board in Ratings.Boards) { Key(board.Key); Facts(board.Label, board.Description); }
        foreach (var rating in Ratings.Ratings)
        {
            Facts(rating.Name, rating.Description); Key(rating.Code, 32);
            if (rating.MinimumAge < 0 || rating.Designation?.Length > 64 || !Ratings.Boards.Any(x => x.Key == rating.Board)) throw new InvalidDataException("Invalid rating classification or unresolved board.");
        }
    }
}
