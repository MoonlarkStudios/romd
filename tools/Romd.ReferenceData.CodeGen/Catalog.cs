using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Romd.ReferenceData.CodeGen;

/// <summary>Shared compiler/CLI input model. No network, clock, or filesystem access.</summary>
public sealed class Catalog
{
    public JObject Systems { get; }
    public JObject Ratings { get; }
    public IReadOnlyDictionary<string, JObject> Documents { get; }
    public Catalog(IReadOnlyDictionary<string, string> inputs)
    {
        var documents = new Dictionary<string, JObject>(StringComparer.Ordinal);
        foreach (var name in new[] { "version", "systems", "companies", "regions", "languages", "ratings" })
        {
            if (!inputs.TryGetValue(name, out var json)) throw new ArgumentException("Missing catalog: " + name);
            documents.Add(name, JObject.Parse(json, new JsonLoadSettings { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error }));
        }
        Documents = documents;
        if ((int?)documents["version"]["schemaVersion"] != 1) throw new ArgumentException("Unsupported reference schema");
        Require(documents["version"]["catalogVersion"]?.Type == JTokenType.Integer && (int)documents["version"]["catalogVersion"]! > 0, "Invalid catalog version");
        foreach (var kind in new[] { "systems", "companies", "regions", "languages" })
            foreach (var row in documents[kind].Properties())
            {
                Require(!row.Name.StartsWith("local-", StringComparison.OrdinalIgnoreCase), "Built-in keys cannot use local-: " + row.Name);
                Require(row.Value["retired"] == null || row.Value["retired"]!.Type == JTokenType.Boolean, "Invalid retirement flag: " + row.Name);
            }
        Systems = documents["systems"];
        Ratings = documents["ratings"];
        var symbols = new HashSet<string>(StringComparer.Ordinal);
        foreach (var system in Systems.Properties())
        {
            Require(Regex.IsMatch(system.Name, "^[a-z0-9]+(-[a-z0-9]+)*$"), "Invalid system key: " + system.Name);
            var symbol = Text(system.Value, "symbol");
            Require(Regex.IsMatch(symbol, "^[A-Z][A-Za-z0-9]*$") && symbols.Add(symbol), "Invalid/duplicate symbol: " + symbol);
            Text(system.Value, "name");
            Require(Text(system.Value, "compactLabel").Length <= 40, "Compact label too long");
            Require(system.Value["monochrome"]?.Type == JTokenType.Boolean, "Missing monochrome flag");
            ValidateIcon(system.Value);
            foreach (var alias in Array(system.Value, "aliases")) Require(alias.Type == JTokenType.String, "Invalid system alias");
            Require(system.Value["providerMappings"] is JObject, "Missing provider mappings");
            foreach (var manufacturer in Array(system.Value, "manufacturerIds"))
                Require(documents["companies"].Property((string)manufacturer!) != null, "Unknown manufacturer: " + manufacturer);
        }
        var boards = new HashSet<string>(StringComparer.Ordinal);
        var values = new HashSet<int>();
        foreach (var board in Array(Ratings, "boards"))
        {
            var key = Text(board, "key");
            Require(Regex.IsMatch(key, "^[A-Z][A-Za-z0-9]*$") && boards.Add(key), "Invalid/duplicate board: " + key);
            Require(board["value"]?.Type == JTokenType.Integer && values.Add((int)board["value"]!), "Duplicate/invalid board value");
            Text(board, "label");
            Array(board, "prefixes");
        }
        var codes = new HashSet<string>(StringComparer.Ordinal);
        var aliases = new HashSet<string>(StringComparer.Ordinal);
        foreach (var rating in Array(Ratings, "ratings"))
        {
            Text(rating, "name");
            Text(rating, "description");
            ValidateIcon(rating);
            var board = Text(rating, "board");
            Require(boards.Contains(board), "Unknown board: " + board);
            Require(codes.Add(board + ":" + Text(rating, "code")), "Duplicate rating");
            var designation = Text(rating, "designation");
            Require(new[] { "Rated", "RatingPending", "RefusedClassification" }.Contains(designation), "Unknown designation");
            Require(designation == "Rated" ? rating["minimumAge"]?.Type == JTokenType.Integer && (int)rating["minimumAge"]! >= 0 : rating["minimumAge"]?.Type == JTokenType.Null, "Invalid rating age");
            foreach (var alias in Array(rating, "aliases"))
                Require(alias.Type == JTokenType.String && aliases.Add(board + ":" + (string)alias!), "Duplicate rating alias");
        }
    }
    private static void ValidateIcon(JToken item)
    {
        var path = item["iconPath"];
        Require(path?.Type == JTokenType.Null || path?.Type == JTokenType.String &&
            Regex.IsMatch((string)path!, @"^(platforms|ratings)/[A-Za-z0-9_+./-]+\.(png|svg)$") &&
            !((string)path!).Split('/').Contains(".."), "Invalid icon path");
    }
    public static string Text(JToken item, string field) => item[field]?.Type == JTokenType.String && !string.IsNullOrWhiteSpace((string?)item[field]) ? (string)item[field]! : throw new ArgumentException("Missing text: " + field);
    public static JArray Array(JToken item, string field) => item[field] as JArray ?? throw new ArgumentException("Missing array: " + field);
    private static void Require(bool valid, string message) { if (!valid) throw new ArgumentException(message); }
    public static string Quote(string value) => JsonConvert.SerializeObject(value);
    public const string Header = "// Generated from reference-data/catalog. Do not edit.\n";

    public string CSharpPlatforms()
    {
        var text = new StringBuilder(Header + "namespace Romd.Domain.Source.Platform;\npublic static class PlatformIds\n{\n");
        foreach (var entry in Systems.Properties().OrderBy(p => p.Name, StringComparer.Ordinal))
            text.Append("    public const string ").Append(Text(entry.Value, "symbol")).Append(" = ").Append(Quote(entry.Name)).Append(";\n");
        return text.Append("}\n").ToString();
    }
    public string CSharpRatings()
    {
        var text = new StringBuilder(Header + "using System.Collections.Generic;\nusing System.Collections.Frozen;\nnamespace Romd.Domain.Catalog.Ratings;\npublic enum RatingBoard\n{\n");
        foreach (var board in Array(Ratings, "boards")) text.Append(Text(board,"key")).Append(" = ").Append((int)board["value"]!).Append(",\n");
        text.Append("}\npublic static partial class RatingBoardCatalog\n{\npublic static IReadOnlyList<RatingBoard> DefaultBoardPreference { get; } = [");
        text.Append(string.Join(",", Array(Ratings,"boards").Select(b => "RatingBoard." + Text(b,"key"))));
        text.Append("];\nprivate static readonly FrozenDictionary<RatingBoard,string[]> BoardPrefixes = new Dictionary<RatingBoard,string[]> {\n");
        foreach (var board in Array(Ratings,"boards")) text.Append("[RatingBoard.").Append(Text(board,"key")).Append("] = [").Append(string.Join(",", Array(board,"prefixes").Select(v=>Quote((string)v!)))).Append("],\n");
        text.Append("}.ToFrozenDictionary();\nprivate static void AddDefinitions(System.Action<RatingBoard,string,RatingDesignation,int?,string[]> add)\n{\n");
        foreach (var row in Array(Ratings,"ratings"))
            text.Append("add(RatingBoard.").Append(Text(row,"board")).Append(',').Append(Quote(Text(row,"code"))).Append(",RatingDesignation.").Append(Text(row,"designation")).Append(',').Append(row["minimumAge"]!.Type == JTokenType.Null ? "null" : row["minimumAge"]!.ToString()).Append(",[").Append(string.Join(",",Array(row,"aliases").Select(v=>Quote((string)v!)))).Append("]);\n");
        return text.Append("}\n}\n").ToString();
    }
    public string Snapshot()
    {
        var result = new JObject { ["schemaVersion"] = 1, ["catalogVersion"] = Documents["version"]["catalogVersion"]!.DeepClone() };
        foreach(var name in new[] { "systems", "companies", "regions", "languages", "ratings" }) result[name] = Documents[name].DeepClone();
        return result.ToString(Formatting.Indented) + "\n";
    }
}
