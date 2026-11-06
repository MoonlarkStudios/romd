using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json.Nodes;
using Moonlark.Native.Engineering.Chdman;
using Moonlark.Native.Engineering.Core;

namespace Moonlark.Native.Engineering.Fixtures;

internal sealed record FixtureRequest(string Chdman, string Manifest, string Output, string LogPath);

/// <summary>Size, SHA-1 and SHA-256 of one generated file, by its path relative to the fixture directory.</summary>
internal sealed record FixtureFile(string Path, long Bytes, string Sha1, string Sha256)
{
    internal static FixtureFile Read(string directory, string name)
    {
        byte[] data = File.ReadAllBytes(System.IO.Path.Combine(directory, name));
        return new FixtureFile(name, data.Length, ChdHeader.Sha1(data), Digest.Sha256(data));
    }

    internal JsonObject ToJson() => new() { ["path"] = Path, ["bytes"] = Bytes, ["sha1"] = Sha1, ["sha256"] = Sha256 };
}

/// <summary>Generates deterministic synthetic CHDs with the pinned chdman and independent header/hash evidence.</summary>
internal static class FixtureGenerator
{
    internal const string GeneratorVersion = "libchdr-synthetic/v3";
    internal const string ManifestName = "fixtures-manifest.json";
    internal const uint DvdHunkBytes = 16384;
    internal const uint CdHunkBytes = 19584;
    internal static readonly ImmutableArray<string> DvdCodecs = ["lzma", "zlib", "huff", "flac", "zstd", "none"];
    internal static readonly ImmutableArray<string> CdCodecs = ["cdlz", "cdzl", "cdfl", "cdzs"];

    /// <summary>DVD recipe geometries ROMD may adopt beyond the codec fixtures' 16 KiB hunks, as (codec, hunk bytes); each
    /// is fixture <see cref="DvdRecipeStem"/>, a fuzz seed for that exact geometry.</summary>
    internal static readonly ImmutableArray<(string Codec, uint HunkBytes)> DvdRecipes = [("zstd", 4096)];

    internal static string DvdRecipeStem((string Codec, uint HunkBytes) recipe) =>
        "dvd-" + recipe.Codec + "-hs" + recipe.HunkBytes.ToString(CultureInfo.InvariantCulture);
    private static readonly ImmutableArray<string> SourceNames = ["dvd-source.iso", "cd-source.bin", "cd-source.cue", "cd-subcode-source.bin", "cd-subcode-source.toc"];
    private static readonly TimeSpan NegativeTimeout = TimeSpan.FromSeconds(120);

    private sealed record Session(string Tool, string Output, IReadOnlyDictionary<string, string> Environment, TextWriter Log)
    {
        internal Result<string> Run(params string[] arguments) => ProcessRunner.Run([Tool, .. arguments], Environment, Log, Output);

        internal string PathOf(string name) => System.IO.Path.Combine(Output, name);
    }

    internal static Result<JsonObject> Run(string root, IReadOnlyDictionary<string, string> environment, FixtureRequest request)
    {
        Result<JsonObject> receipt = VerifyTool(root, request.Chdman, request.Manifest);
        if (!receipt.Succeeded)
            return receipt.Failure;
        string tool = ArtifactsPath.Resolve(request.Chdman);
        Result<string> output = ValidateOutput(root, request.Output);
        if (!output.Succeeded)
            return output.Failure;
        Result<IReadOnlyDictionary<string, string>> env = BuildEnvironment.Create(environment);
        if (!env.Succeeded)
            return env.Failure;
        if (Directory.Exists(output.Value))
            Directory.Delete(output.Value, recursive: true);
        Directory.CreateDirectory(output.Value);
        File.WriteAllBytes(Path.Combine(output.Value, "dvd-source.iso"), SyntheticSources.Dvd());
        File.WriteAllBytes(Path.Combine(output.Value, "cd-source.bin"), SyntheticSources.Cd());
        File.WriteAllText(Path.Combine(output.Value, "cd-source.cue"), SyntheticSources.CdCue);
        foreach ((string name, byte[] bytes) in CdEdgeFixtures.Sources())
            File.WriteAllBytes(Path.Combine(output.Value, name), bytes);
        Result<(JsonArray Fixtures, JsonArray Negative)> evidence;
        using (var log = new StreamWriter(request.LogPath, append: false))
            evidence = Generate(new Session(tool, output.Value, env.Value, log));
        if (!evidence.Succeeded)
            return evidence.Failure;
        var manifest = new JsonObject
        {
            ["schemaVersion"] = 1,
            ["synthetic"] = true,
            ["generatorVersion"] = GeneratorVersion,
            ["tool"] = receipt.Value,
            ["sources"] = new JsonArray([.. SourceNames.Concat(CdEdgeFixtures.Sources().Keys).Select(name => (JsonNode?)FixtureFile.Read(output.Value, name).ToJson())]),
            ["fixtures"] = evidence.Value.Fixtures,
            ["negativeVerification"] = evidence.Value.Negative,
        };
        File.WriteAllText(Path.Combine(output.Value, ManifestName), ReceiptJson.Serialize(manifest));
        return manifest;
    }

    /// <summary>The tool must match the pin, the current chdman build-recipe version and its receipt, which is embedded verbatim.</summary>
    internal static Result<JsonObject> VerifyTool(string root, string tool, string manifest)
    {
        JsonObject receipt = JsonNode.Parse(File.ReadAllText(manifest)) as JsonObject ?? throw new InvalidDataException("Tool manifest must be a JSON object");
        if (Check.That(JsonNode.DeepEquals(receipt["pin"], MamePin.ReadDocument(root)) && Integer(receipt["schemaVersion"]) == ChdmanBuild.ReceiptSchemaVersion
            && Integer(receipt["recipeVersion"]) == ChdmanBuild.RecipeVersion, "Tool pin/manifest mismatch") is { } pin)
            return pin;
        if (Check.That(File.Exists(tool) && !ArtifactsPath.IsLink(tool), "Tool must be a regular file") is { } regular)
            return regular;
        JsonNode? binary = receipt["binary"];
        if (Check.That(Integer(binary?["bytes"]) == new FileInfo(tool).Length && Text(binary?["sha256"]) == Digest.Sha256File(tool),
            "Tool binary digest/size mismatch") is { } digest)
            return digest;
        return receipt;
    }

    internal static Result<string> ValidateOutput(string root, string output)
    {
        Result<string> validated = ArtifactsPath.ValidateDirectory(output, root);
        if (!validated.Succeeded)
            return validated.Failure;
        string fixtures = ArtifactsPath.Resolve(Path.Combine(root, "artifacts", "fixtures"));
        if (Check.That(ArtifactsPath.IsWithin(validated.Value, fixtures)
            && !string.Equals(Path.TrimEndingDirectorySeparator(validated.Value), Path.TrimEndingDirectorySeparator(fixtures), StringComparison.Ordinal),
            "Output must be a fixtures subdirectory") is { } outside)
            return outside;
        return validated.Value;
    }

    /// <summary>A zero exit is not success: both hash confirmations and no error or declined verification are required.</summary>
    internal static bool VerifySucceeded(string output) =>
        output.Contains("Raw SHA1 verification successful!", StringComparison.Ordinal)
        && output.Contains("Overall SHA1 verification successful!", StringComparison.Ordinal)
        && !output.Contains("Error:", StringComparison.Ordinal) && !output.Contains("No verification", StringComparison.Ordinal);

    private static Result<(JsonArray Fixtures, JsonArray Negative)> Generate(Session session)
    {
        var fixtures = new JsonArray();
        foreach ((string media, string codec) in DvdCodecs.Select(codec => ("dvd", codec)).Concat(CdCodecs.Select(codec => ("cd", codec))))
        {
            Result<JsonObject> fixture = MakeFixture(session, media, codec, media + "-" + codec, media == "dvd" ? DvdHunkBytes : CdHunkBytes);
            if (!fixture.Succeeded)
                return fixture.Failure;
            fixtures.Add(fixture.Value);
        }
        Result<JsonObject> subcode = MakeSubcodeFixture(session);
        if (!subcode.Succeeded)
            return subcode.Failure;
        fixtures.Add(subcode.Value);
        foreach (string stem in CdEdgeFixtures.Stems)
        {
            Result<JsonObject> edge = MakeEdgeFixture(session, stem);
            if (!edge.Succeeded)
                return edge.Failure;
            fixtures.Add(edge.Value);
        }
        foreach ((string Codec, uint HunkBytes) recipe in DvdRecipes)
        {
            Result<JsonObject> fixture = MakeFixture(session, "dvd", recipe.Codec, DvdRecipeStem(recipe), recipe.HunkBytes);
            if (!fixture.Succeeded)
                return fixture.Failure;
            fixtures.Add(fixture.Value);
        }
        Result<JsonArray> negative = VerifyNegativeHashes(session);
        if (!negative.Succeeded)
            return negative.Failure;
        return (fixtures, negative.Value);
    }

    private static Result<JsonObject> MakeFixture(Session session, string media, string codec, string stem, uint hunkBytes)
    {
        bool dvd = media == "dvd";
        Result<string> create = session.Run("create" + media, "-i", dvd ? "dvd-source.iso" : "cd-source.cue", "-o", stem + ".chd",
            "-c", codec, "-hs", hunkBytes.ToString(CultureInfo.InvariantCulture), "-np", "1");
        if (!create.Succeeded)
            return create.Failure;
        Result<ChdHeader> header = ChdHeader.Read(session.PathOf(stem + ".chd"));
        if (!header.Succeeded)
            return header.Failure;
        Result<string> verification = session.Run("verify", "-i", stem + ".chd");
        if (!verification.Succeeded)
            return verification.Failure;
        bool verified = codec != "none";
        if ((verified
            ? Check.That(VerifySucceeded(verification.Value), "chdman did not verify both hashes")
            : Check.That(verification.Value.Contains("No verification to be done; CHD is uncompressed", StringComparison.Ordinal),
                "Unexpected uncompressed verification response")) is { } response)
            return response;
        Result<string> raw = session.Run("extractraw", "-i", stem + ".chd", "-o", stem + ".logical");
        if (!raw.Succeeded)
            return raw.Failure;
        FixtureFile logical = FixtureFile.Read(session.Output, stem + ".logical");
        if (Check.That((ulong)logical.Bytes == header.Value.LogicalBytes, "Logical extraction length mismatch") is { } length)
            return length;
        string computed = ChdHeader.ComputeOverallSha1(logical.Sha1, header.Value.Metadata);
        if (verified && Check.That(logical.Sha1 == header.Value.RawSha1, "Independently computed raw SHA1 mismatch") is { } rawMismatch)
            return rawMismatch;
        if (verified && Check.That(computed == header.Value.OverallSha1, "Independently computed overall SHA1 mismatch") is { } overallMismatch)
            return overallMismatch;
        Result<FixtureFile> extracted = dvd
            ? Recover(session, ["extractdvd", "-i", stem + ".chd", "-o", stem + ".extracted.iso"], stem + ".extracted.iso",
                SyntheticSources.Dvd(), "DVD bytes were not exactly recovered")
            : Recover(session, ["extractcd", "-i", stem + ".chd", "-o", stem + ".extracted.cue", "-ob", stem + ".extracted.bin"], stem + ".extracted.bin",
                SyntheticSources.Cd(), "CD source bytes were not exactly recovered");
        if (!extracted.Succeeded)
            return extracted.Failure;
        return Evidence(media, codec, session, stem, header.Value, logical, computed, verified, extracted.Value);
    }

    private static Result<JsonObject> MakeSubcodeFixture(Session session)
    {
        byte[] source = SyntheticSources.CdSubcode();
        File.WriteAllBytes(session.PathOf("cd-subcode-source.bin"), source);
        File.WriteAllText(session.PathOf("cd-subcode-source.toc"), SyntheticSources.CdSubcodeToc);
        Result<string> create = session.Run("createcd", "-i", "cd-subcode-source.toc", "-o", "cd-subcode.chd", "-c", "cdlz", "-hs", "19584", "-np", "1");
        if (!create.Succeeded)
            return create.Failure;
        Result<ChdHeader> header = ChdHeader.Read(session.PathOf("cd-subcode.chd"));
        if (!header.Succeeded)
            return header.Failure;
        Result<string> verification = session.Run("verify", "-i", "cd-subcode.chd");
        if (!verification.Succeeded)
            return verification.Failure;
        if (Check.That(VerifySucceeded(verification.Value), "chdman did not verify both subcode fixture hashes") is { } response)
            return response;
        Result<string> raw = session.Run("extractraw", "-i", "cd-subcode.chd", "-o", "cd-subcode.logical");
        if (!raw.Succeeded)
            return raw.Failure;
        if (Check.That(File.ReadAllBytes(session.PathOf("cd-subcode.logical")).AsSpan().SequenceEqual(source),
            "Stored frame/subcode bytes were not exactly recovered") is { } stored)
            return stored;
        FixtureFile logical = FixtureFile.Read(session.Output, "cd-subcode.logical");
        string computed = ChdHeader.ComputeOverallSha1(logical.Sha1, header.Value.Metadata);
        if (Check.That(logical.Sha1 == header.Value.RawSha1 && computed == header.Value.OverallSha1,
            "Independent subcode fixture hash mismatch") is { } mismatch)
            return mismatch;
        Result<FixtureFile> extracted = Recover(session, ["extractcd", "-i", "cd-subcode.chd", "-o", "cd-subcode.extracted.toc", "-ob", "cd-subcode.extracted.bin"],
            "cd-subcode.extracted.bin", source, "TOC frame/subcode bytes were not exactly recovered");
        if (!extracted.Succeeded)
            return extracted.Failure;
        return Evidence("cd", "cdlz", session, "cd-subcode", header.Value, logical, computed, verified: true, extracted.Value);
    }

    private static Result<JsonObject> MakeEdgeFixture(Session session, string stem)
    {
        Result<string> create = session.Run("createcd", "-i", stem + ".cue", "-o", stem + ".chd", "-c", "cdlz", "-hs", "19584", "-np", "1");
        if (!create.Succeeded)
            return create.Failure;
        Result<ChdHeader> header = ChdHeader.Read(session.PathOf(stem + ".chd"));
        if (!header.Succeeded)
            return header.Failure;
        if (Check.That(CdEdgeFixtures.MetadataMatches(stem, header.Value), "Edge fixture metadata mismatch") is { } metadata)
            return metadata;
        Result<string> verification = session.Run("verify", "-i", stem + ".chd");
        if (!verification.Succeeded)
            return verification.Failure;
        if (Check.That(VerifySucceeded(verification.Value), "chdman did not verify both edge fixture hashes") is { } response)
            return response;
        Result<string> raw = session.Run("extractraw", "-i", stem + ".chd", "-o", stem + ".logical");
        if (!raw.Succeeded)
            return raw.Failure;
        byte[] expected = CdEdgeFixtures.Logical(stem);
        if (Check.That(header.Value.LogicalBytes == (ulong)expected.Length && header.Value.HunkBytes == 19584 && header.Value.UnitBytes == 2448
            && File.ReadAllBytes(session.PathOf(stem + ".logical")).AsSpan().SequenceEqual(expected), "Edge fixture source projection mismatch") is { } projection)
            return projection;
        FixtureFile logical = FixtureFile.Read(session.Output, stem + ".logical");
        string computed = ChdHeader.ComputeOverallSha1(logical.Sha1, header.Value.Metadata);
        if (Check.That(logical.Sha1 == header.Value.RawSha1 && computed == header.Value.OverallSha1, "Independent edge fixture hash mismatch") is { } mismatch)
            return mismatch;
        if (stem == "cd-edge-subcode")
        {
            string extractedName = stem + ".extracted.toc.bin";
            Result<string> toc = session.Run("extractcd", "-i", stem + ".chd", "-o", stem + ".extracted.toc", "-ob", extractedName);
            if (!toc.Succeeded)
                return toc.Failure;
            byte[] recovered = File.ReadAllBytes(session.PathOf(extractedName));
            if (Check.That(recovered.Length == 14 * 2448, "Edge subcode extraction length mismatch") is { } length)
                return length;
            CdEdgeFixtures.SwapSubcodeAudio(recovered);
            if (Check.That(recovered.AsSpan().SequenceEqual(CdEdgeFixtures.Source(stem)), "Edge CD source bytes were not exactly recovered") is { } recovery)
                return recovery;
            File.WriteAllBytes(session.PathOf(stem + ".extracted.bin"), recovered);
            JsonObject evidence = Evidence("cd", "cdlz", session, stem, header.Value, logical, computed, verified: true, FixtureFile.Read(session.Output, stem + ".extracted.bin"));
            evidence["rawExtracted"] = FixtureFile.Read(session.Output, extractedName).ToJson();
            evidence["recoveryTransform"] = "swap-audio-16-after-track1";
            return evidence;
        }
        Result<FixtureFile> extracted = Recover(session, ["extractcd", "-i", stem + ".chd", "-o", stem + ".extracted.cue", "-ob", stem + ".extracted.bin"],
            stem + ".extracted.bin", CdEdgeFixtures.Source(stem), "Edge CD source bytes were not exactly recovered");
        if (!extracted.Succeeded)
            return extracted.Failure;
        return Evidence("cd", "cdlz", session, stem, header.Value, logical, computed, verified: true, extracted.Value);
    }

    /// <summary>Extracts with the media's own command and requires the exact source bytes back.</summary>
    private static Result<FixtureFile> Recover(Session session, string[] arguments, string extracted, byte[] expected, string message)
    {
        Result<string> run = session.Run(arguments);
        if (!run.Succeeded)
            return run.Failure;
        if (Check.That(File.ReadAllBytes(session.PathOf(extracted)).AsSpan().SequenceEqual(expected), message) is { } recovered)
            return recovered;
        return FixtureFile.Read(session.Output, extracted);
    }

    private static JsonObject Evidence(string media, string codec, Session session, string stem, ChdHeader header, FixtureFile logical,
        string computed, bool verified, FixtureFile extracted) => new()
        {
            ["media"] = media,
            ["codec"] = codec,
            ["chd"] = FixtureFile.Read(session.Output, stem + ".chd").ToJson(),
            ["header"] = header.ToJson(),
            ["logical"] = logical.ToJson(),
            ["computedOverallSha1"] = computed,
            ["chdmanVerifiedBothHashes"] = verified,
            ["exactSourceRecovery"] = true,
            ["extracted"] = extracted.ToJson(),
        };

    /// <summary>Header-hash corruptions that the pinned chdman reports with exit zero; the verification parser must reject each.</summary>
    private static Result<JsonArray> VerifyNegativeHashes(Session session)
    {
        byte[] original = File.ReadAllBytes(session.PathOf("dvd-lzma.chd"));
        (string Name, int Offset, string Diagnostic)[] cases =
        [
            ("raw-mismatch", 64, "Error: Raw SHA1 in header"),
            ("overall-mismatch", 84, "Error: Overall SHA1 in header"),
            ("raw-missing", 64, "No verification to be done; CHD has no checksum"),
        ];
        var results = new JsonArray();
        foreach ((string name, int offset, string diagnostic) in cases)
        {
            byte[] data = [.. original];
            if (name == "raw-missing")
                data.AsSpan(64, 20).Clear();
            else
                data[offset] ^= 1;
            string chd = "negative-" + name + ".chd";
            File.WriteAllBytes(session.PathOf(chd), data);
            string[] command = [session.Tool, "verify", "-i", chd];
            ProcessOutput result = ProcessRunner.Execute(command, session.Environment, session.Log, session.Output, NegativeTimeout);
            if (Check.That(!result.TimedOut, "Command timed out: " + ProcessRunner.Display(command)) is { } timeout)
                return timeout;
            if (Check.That(result.Combined.Contains(diagnostic, StringComparison.Ordinal) && !VerifySucceeded(result.Combined),
                "Negative verification was not rejected: " + name) is { } accepted)
                return accepted;
            results.Add(new JsonObject
            {
                ["case"] = name,
                ["chd"] = FixtureFile.Read(session.Output, chd).ToJson(),
                ["exitCode"] = result.ExitCode,
                ["diagnostic"] = diagnostic,
                ["acceptedByVerificationParser"] = false,
            });
        }
        return results;
    }

    private static long Integer(JsonNode? node) => node is JsonValue value && value.TryGetValue(out long number) ? number : -1;

    private static string Text(JsonNode? node) => node is JsonValue value && value.TryGetValue(out string? text) ? text : "";
}
