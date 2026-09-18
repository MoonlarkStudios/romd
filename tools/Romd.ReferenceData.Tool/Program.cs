using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Romd.ReferenceData.CodeGen;

var root = Path.GetFullPath(args.FirstOrDefault(a => a != "--check") ?? ".");
var check = args.Contains("--check");
var path = Path.Combine(root, "reference-data/catalog");
var catalog = new Catalog(Directory.GetFiles(path,"*.json").ToDictionary(file => Path.GetFileNameWithoutExtension(file)!, File.ReadAllText));
var systems = catalog.Systems.Properties().ToArray();
var ts = Catalog.Header + "export const systemKeys = " + new JObject(systems.Select(s => new JProperty(Catalog.Text(s.Value,"symbol"),s.Name))).ToString(Formatting.Indented) + " as const;\n";
ts += "export type KnownSystemKey = typeof systemKeys[keyof typeof systemKeys];\n";
ts += "export const ratingBoards = " + catalog.Ratings["boards"]!.ToString(Formatting.Indented) + " as const;\n";
string Dq(string value) => "'" + value.Replace("\\","\\\\").Replace("'","\\'").Replace("$","\\$") + "'";
var dart = Catalog.Header + "abstract final class SystemKeys {\n" + string.Join("", systems.Select(s => $"  static const {Catalog.Text(s.Value,"symbol")} = {Dq(s.Name)};\n")) + "}\n";
dart += "const canonicalRatingBoards = <String, int>{\n" + string.Join("",Catalog.Array(catalog.Ratings,"boards").Select(b => $"  {Dq(Catalog.Text(b,"key"))}: {b["value"]},\n")) + "};\n";
var outputs = new Dictionary<string,string> {
 ["web/packages/romd-foundation/src/generated/referenceCatalog.ts"] = ts,
 ["clients/romd_console/lib/src/data/generated/reference_catalog.dart"] = dart,
 ["reference-data/dist/reference-data.json"] = catalog.Snapshot(),
 ["reference-data/dist/system-keys.json"] = new JObject { ["schemaVersion"] = 1, ["systems"] = new JArray(systems.Select(s=>s.Name)) }.ToString(Formatting.Indented) + "\n"
};
foreach (var (file, content) in outputs) {
 var target = Path.Combine(root,file);
 if(check) { if(!File.Exists(target) || File.ReadAllText(target)!=content) throw new InvalidDataException("Stale generated reference data: " + file); }
 else { Directory.CreateDirectory(Path.GetDirectoryName(target)!); File.WriteAllText(target,content); }
}
Console.WriteLine($"{(check ? "Checked" : "Generated")} reference catalog: {systems.Length} systems, {Catalog.Array(catalog.Ratings,"ratings").Count} ratings.");
