using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Moonlark.Native.Engineering.Chdman;
using Moonlark.Native.Engineering.Core;

namespace Moonlark.Native.Engineering.Fixtures;

/// <summary>Checks a transferred synthetic fixture set without running or claiming to verify its recorded tool executable.</summary>
internal static class FixtureVerification
{
    private sealed record ExpectedFixture(string Media, string Codec, string Stem, byte[] Source, uint? HunkBytes = null);

    internal static Result<JsonObject> Verify(string root, string directory)
    {
        try
        {
            directory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
            Require(Directory.Exists(directory) && !ArtifactsPath.IsLink(directory), "Fixture directory must be a nonlink directory");
            string manifestPath = RegularFile(directory, FixtureGenerator.ManifestName);
            JsonObject manifest = Object(JsonNode.Parse(File.ReadAllText(manifestPath)), "Fixture manifest");
            Require(Integer(manifest["schemaVersion"]) == 1 && Boolean(manifest["synthetic"], true)
                && Text(manifest["generatorVersion"]) == FixtureGenerator.GeneratorVersion, "Fixture schema/synthetic/generator mismatch");
            JsonObject tool = Object(manifest["tool"], "Tool receipt");
            Require(Integer(tool["schemaVersion"]) == ChdmanBuild.ReceiptSchemaVersion && Integer(tool["recipeVersion"]) == ChdmanBuild.RecipeVersion
                && JsonNode.DeepEquals(tool["pin"], MamePin.ReadDocument(root)), "Fixture tool pin/manifest mismatch");

            Dictionary<string, byte[]> sources = new(StringComparer.Ordinal)
            {
                ["dvd-source.iso"] = SyntheticSources.Dvd(),
                ["cd-source.bin"] = SyntheticSources.Cd(),
                ["cd-source.cue"] = Encoding.ASCII.GetBytes(SyntheticSources.CdCue),
                ["cd-subcode-source.bin"] = SyntheticSources.CdSubcode(),
                ["cd-subcode-source.toc"] = Encoding.ASCII.GetBytes(SyntheticSources.CdSubcodeToc),
            };
            foreach ((string name, byte[] bytes) in CdEdgeFixtures.Sources())
                sources.Add(name, bytes);
            Dictionary<string, JsonObject> sourceInventory = Inventory(manifest["sources"], item => Text(item["path"]), "source");
            Require(sourceInventory.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(sources.Keys), "Incomplete/unknown source inventory");
            foreach ((string name, byte[] expected) in sources)
                Require(ReadFile(directory, sourceInventory[name], name).AsSpan().SequenceEqual(expected), "Synthetic source bytes mismatch: " + name);

            ExpectedFixture[] expectedFixtures =
            [
                .. FixtureGenerator.DvdCodecs.Select(codec => new ExpectedFixture("dvd", codec, "dvd-" + codec, sources["dvd-source.iso"])),
                .. FixtureGenerator.CdCodecs.Select(codec => new ExpectedFixture("cd", codec, "cd-" + codec, sources["cd-source.bin"])),
                new("cd", "cdlz", "cd-subcode", sources["cd-subcode-source.bin"]),
                .. CdEdgeFixtures.Stems.Select(stem => new ExpectedFixture("cd", "cdlz", stem, CdEdgeFixtures.Source(stem))),
                .. FixtureGenerator.DvdRecipes.Select(recipe => new ExpectedFixture("dvd", recipe.Codec, FixtureGenerator.DvdRecipeStem(recipe),
                    sources["dvd-source.iso"], recipe.HunkBytes)),
            ];
            Dictionary<string, JsonObject> fixtures = Inventory(manifest["fixtures"], item => Text(Object(item["chd"], "CHD file")["path"]), "fixture");
            Require(fixtures.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(expectedFixtures.Select(item => item.Stem + ".chd")),
                "Incomplete/unknown fixture inventory");
            foreach (ExpectedFixture expected in expectedFixtures)
                VerifyFixture(directory, fixtures[expected.Stem + ".chd"], expected);
            VerifyNegative(directory, manifest["negativeVerification"], File.ReadAllBytes(Path.Combine(directory, "dvd-lzma.chd")));
            return manifest;
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or ArgumentException)
        {
            return new Failure("Fixture verification failed: " + exception.Message);
        }
    }

    private static void VerifyFixture(string directory, JsonObject fixture, ExpectedFixture expected)
    {
        Require(Text(fixture["media"]) == expected.Media && Text(fixture["codec"]) == expected.Codec, "Fixture media/codec mismatch");
        byte[] chd = ReadFile(directory, Object(fixture["chd"], "CHD file"), expected.Stem + ".chd");
        byte[] logical = ReadFile(directory, Object(fixture["logical"], "Logical file"), expected.Stem + ".logical");
        byte[] extracted = ReadFile(directory, Object(fixture["extracted"], "Extracted file"),
            expected.Stem + ".extracted." + (expected.Media == "dvd" ? "iso" : "bin"));
        Require(extracted.AsSpan().SequenceEqual(expected.Source) && Boolean(fixture["exactSourceRecovery"], true), "Exact source recovery mismatch");
        if (expected.Stem == "cd-edge-subcode")
        {
            byte[] rawExtraction = ReadFile(directory, Object(fixture["rawExtracted"], "Raw TOC extraction"), expected.Stem + ".extracted.toc.bin");
            Require(rawExtraction.Length == 14 * 2448 && Text(fixture["recoveryTransform"]) == "swap-audio-16-after-track1", "Subcode extraction transform mismatch");
            CdEdgeFixtures.SwapSubcodeAudio(rawExtraction);
            Require(rawExtraction.AsSpan().SequenceEqual(extracted), "Raw TOC extraction mismatch");
        }
        Result<ChdHeader> parsed = ChdHeader.Parse(chd);
        Require(parsed.Succeeded, parsed.Succeeded ? "" : parsed.Failure.Message);
        Span<byte> compressors = stackalloc byte[16];
        compressors.Clear();
        if (expected.Codec != "none")
            Encoding.ASCII.GetBytes(expected.Codec, compressors[..4]);
        Require(chd.AsSpan(16, 16).SequenceEqual(compressors), "Fixture CHD compressor slots mismatch: " + expected.Stem);
        ChdHeader header = parsed.Value;
        Require(JsonNode.DeepEquals(header.ToJson(), fixture["header"]), "Recorded CHD header differs from file");
        Require(header.LogicalBytes == (ulong)logical.Length
            && header.HunkBytes == (expected.HunkBytes ?? (expected.Media == "dvd" ? FixtureGenerator.DvdHunkBytes : FixtureGenerator.CdHunkBytes))
            && header.UnitBytes == (expected.Media == "dvd" ? 2048U : 2448U)
            && header.ParentSha1 == new string('0', 40), "Fixture header geometry/parent mismatch");
        string raw = ChdHeader.Sha1(logical);
        string overall = ChdHeader.ComputeOverallSha1(raw, header.Metadata);
        Require(Text(fixture["computedOverallSha1"]) == overall, "Computed overall SHA1 mismatch");
        bool compressed = expected.Codec != "none";
        Require(Boolean(fixture["chdmanVerifiedBothHashes"], compressed), "Fixture hash verification claim mismatch");
        Require(compressed ? header.RawSha1 == raw && header.OverallSha1 == overall
            : header.RawSha1 == new string('0', 40) && header.OverallSha1 == new string('0', 40), "Header/logical SHA1 mismatch");
        byte[] projected = expected.Source;
        if (CdEdgeFixtures.Stems.Contains(expected.Stem))
        {
            projected = CdEdgeFixtures.Logical(expected.Stem);
            Require(CdEdgeFixtures.MetadataMatches(expected.Stem, header), "Edge fixture metadata mismatch");
        }
        else if (expected.Media == "cd" && expected.Stem != "cd-subcode")
            projected = CdEdgeFixtures.Project([(expected.Source[..(16 * 2352)], false, 2352), (expected.Source[(16 * 2352)..], true, 2352)]);
        Require(logical.AsSpan().SequenceEqual(projected), "Logical source projection mismatch");
    }

    private static void VerifyNegative(string directory, JsonNode? node, byte[] original)
    {
        (string Name, int Offset, string Diagnostic)[] expected =
        [
            ("raw-mismatch", 64, "Error: Raw SHA1 in header"),
            ("overall-mismatch", 84, "Error: Overall SHA1 in header"),
            ("raw-missing", 64, "No verification to be done; CHD has no checksum"),
        ];
        Dictionary<string, JsonObject> inventory = Inventory(node, item => Text(item["case"]), "negative verification");
        Require(inventory.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(expected.Select(item => item.Name)), "Incomplete/unknown negative inventory");
        foreach ((string name, int offset, string diagnostic) in expected)
        {
            JsonObject entry = inventory[name];
            Require(Boolean(entry["acceptedByVerificationParser"], false) && Text(entry["diagnostic"]) == diagnostic
                && Integer(entry["exitCode"]) == 0, "Negative verification outcome mismatch: " + name);
            byte[] bytes = ReadFile(directory, Object(entry["chd"], "Negative CHD file"), "negative-" + name + ".chd");
            byte[] mutated = [.. original];
            if (name == "raw-missing")
                mutated.AsSpan(64, 20).Clear();
            else
                mutated[offset] ^= 1;
            Require(bytes.AsSpan().SequenceEqual(mutated), "Negative fixture mutation mismatch: " + name);
        }
    }

    private static byte[] ReadFile(string directory, JsonObject record, string expectedName)
    {
        Require(Text(record["path"]) == expectedName, "Unexpected fixture path: " + Text(record["path"]));
        byte[] data = File.ReadAllBytes(RegularFile(directory, expectedName));
        Require(data.Length > 0 && Integer(record["bytes"]) == data.LongLength
            && Text(record["sha1"]) == ChdHeader.Sha1(data) && Text(record["sha256"]) == Digest.Sha256(data),
            "Fixture file size/digest mismatch: " + expectedName);
        return data;
    }

    private static string RegularFile(string directory, string name)
    {
        // All callers supply an exact inventory leaf, never a manifest-controlled path.
        string path = Path.Combine(directory, name);
        Require(!ArtifactsPath.IsLink(path) && ArtifactsPath.IsRegularFile(path), "Fixture must be a regular nonlink file: " + name);
        return path;
    }

    private static Dictionary<string, JsonObject> Inventory(JsonNode? node, Func<JsonObject, string> key, string label)
    {
        if (node is not JsonArray array)
            throw new InvalidDataException("Missing " + label + " inventory");
        var inventory = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        foreach (JsonNode? value in array)
        {
            JsonObject item = Object(value, label);
            Require(inventory.TryAdd(key(item), item), "Duplicate " + label + " inventory entry");
        }
        return inventory;
    }

    private static JsonObject Object(JsonNode? node, string label) => node as JsonObject ?? throw new InvalidDataException(label + " must be an object");
    private static string Text(JsonNode? node) => node is JsonValue value && value.TryGetValue(out string? text) ? text : "";
    private static long Integer(JsonNode? node) => node is JsonValue value && value.TryGetValue(out long number) ? number : -1;
    private static bool Boolean(JsonNode? node, bool expected) => node is JsonValue value && value.TryGetValue(out bool result) && result == expected;
    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidDataException(message);
    }
}
