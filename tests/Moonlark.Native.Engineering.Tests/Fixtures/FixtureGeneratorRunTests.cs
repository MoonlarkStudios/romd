using System.Text.Json.Nodes;
using Moonlark.Native.Engineering.Core;
using Moonlark.Native.Engineering.Fixtures;
using Xunit;

namespace Moonlark.Native.Engineering.Tests.Fixtures;

/// <summary>
/// The whole fixture generation run against a fake chdman whose prepared outputs start consistent; each failing case makes
/// the fake misbehave in exactly one way, so only the check under test can reject it.
/// </summary>
public sealed class FixtureGeneratorRunTests
{
    private static readonly string[] Stems =
        [.. FixtureGenerator.DvdCodecs.Select(codec => "dvd-" + codec), .. FixtureGenerator.CdCodecs.Select(codec => "cd-" + codec), "cd-subcode", .. CdEdgeFixtures.Stems,
            .. FixtureGenerator.DvdRecipes.Select(FixtureGenerator.DvdRecipeStem)];

    /// <summary>v3 keeps the eleven codec fixtures, the subcode fixture and the four v2 edge geometries, and adds the zstd
    /// DVD recipe at 4 KiB hunks.</summary>
    [Fact]
    public void V3IncludesTheEdgeFixturesAndTheDvdRecipe()
    {
        if (OperatingSystem.IsWindows())
            return;
        using var fake = new FakeChdman();
        JsonObject manifest = fake.Run().Value;
        Assert.Equal("libchdr-synthetic/v3", (string)manifest["generatorVersion"]!);
        string[] names = manifest["fixtures"]!.AsArray().Select(item => (string)item!["chd"]!["path"]!).ToArray();
        Assert.Equal(16, names.Length);
        foreach (string stem in new[] { "multi", "single", "partial", "subcode" })
            Assert.Contains("cd-edge-" + stem + ".chd", names);
        Assert.Contains("dvd-zstd-hs4096.chd", names);
        Assert.Equal(4096U, (uint)manifest["fixtures"]!.AsArray().Single(item => (string)item!["chd"]!["path"]! == "dvd-zstd-hs4096.chd")!["header"]!["hunkBytes"]!);
    }

    /// <summary>Consistent tool outputs produce the full manifest, with independent hashes, exact recovery and every negative case.</summary>
    [Fact]
    public void ConsistentToolOutputsProduceTheFullManifest()
    {
        if (OperatingSystem.IsWindows())
            return;
        using var fake = new FakeChdman();
        JsonObject manifest = fake.Run().Value;
        JsonArray fixtures = manifest["fixtures"]!.AsArray();
        Assert.Equal(Stems.Select(stem => stem + ".chd"), fixtures.Select(fixture => (string)fixture!["chd"]!["path"]!));
        foreach ((string stem, JsonNode? fixture) in Stems.Zip(fixtures))
        {
            SyntheticChd header = fake.Header(stem);
            Assert.Equal(stem != "dvd-none", (bool)fixture!["chdmanVerifiedBothHashes"]!);
            Assert.Equal((header.RawSha1, header.RawSha1), ((string)fixture["header"]!["rawSha1"]!, (string)fixture["logical"]!["sha1"]!));
            Assert.Equal((header.OverallSha1, header.OverallSha1), ((string)fixture["header"]!["overallSha1"]!, (string)fixture["computedOverallSha1"]!));
            byte[] source = CdEdgeFixtures.Stems.Contains(stem) ? CdEdgeFixtures.Source(stem) : stem.StartsWith("dvd-", StringComparison.Ordinal) ? SyntheticSources.Dvd() : stem == "cd-subcode" ? SyntheticSources.CdSubcode() : SyntheticSources.Cd();
            Assert.Equal(Digest.Sha256(source), (string)fixture["extracted"]!["sha256"]!);
        }
        byte[] lzma = File.ReadAllBytes(Path.Combine(fake.Output, "dvd-lzma.chd"));
        JsonArray negative = manifest["negativeVerification"]!.AsArray();
        Assert.Equal(FakeChdman.NegativeCases, negative.Select(entry => ((string)entry!["case"]!, (string)entry["diagnostic"]!)));
        Assert.All(negative, entry => Assert.Equal((0, false), ((int)entry!["exitCode"]!, (bool)entry["acceptedByVerificationParser"]!)));
        Assert.Equal(lzma[64] ^ 1, File.ReadAllBytes(Path.Combine(fake.Output, "negative-raw-mismatch.chd"))[64]);
        Assert.Equal(lzma[84] ^ 1, File.ReadAllBytes(Path.Combine(fake.Output, "negative-overall-mismatch.chd"))[84]);
        Assert.Equal(new byte[20], File.ReadAllBytes(Path.Combine(fake.Output, "negative-raw-missing.chd"))[64..84]);
        Assert.True(JsonNode.DeepEquals(manifest, JsonNode.Parse(File.ReadAllText(Path.Combine(fake.Output, FixtureGenerator.ManifestName)))));
        string log = File.ReadAllText(fake.Log);
        foreach (string command in (string[])["createdvd -i dvd-source.iso -o dvd-lzma.chd -c lzma -hs 16384 -np 1",
            "createdvd -i dvd-source.iso -o dvd-zstd-hs4096.chd -c zstd -hs 4096 -np 1",
            "createcd -i cd-source.cue -o cd-cdzs.chd -c cdzs -hs 19584 -np 1", "createcd -i cd-subcode-source.toc -o cd-subcode.chd -c cdlz -hs 19584 -np 1",
            "extractcd -i cd-subcode.chd -o cd-subcode.extracted.toc -ob cd-subcode.extracted.bin", "verify -i negative-raw-missing.chd"])
            Assert.Contains(command, log, StringComparison.Ordinal);
    }

    /// <summary>An uncompressed fixture has no hashes to compare, but its logical length and recovery are still checked.</summary>
    [Fact]
    public void UncompressedFixtureSkipsOnlyTheHashComparisons()
    {
        if (OperatingSystem.IsWindows())
            return;
        using var fake = new FakeChdman();
        fake.ChangeHeader("dvd-none", header => header with { RawSha1 = new string('0', 40), OverallSha1 = new string('0', 40) });
        Assert.True(fake.Run().Succeeded);
    }

    /// <summary>Each single misbehavior of the tool fails with the check that guards it.</summary>
    [Theory]
    [InlineData("dvd-raw-only-verification", "chdman did not verify both hashes")]
    [InlineData("dvd-verification-error-line", "chdman did not verify both hashes")]
    [InlineData("cd-silent-verification", "chdman did not verify both hashes")]
    [InlineData("uncompressed-verification-claims-success", "Unexpected uncompressed verification response")]
    [InlineData("raw-sha1-header", "Independently computed raw SHA1 mismatch")]
    [InlineData("overall-sha1-header", "Independently computed overall SHA1 mismatch")]
    [InlineData("logical-length", "Logical extraction length mismatch")]
    [InlineData("uncompressed-logical-length", "Logical extraction length mismatch")]
    [InlineData("dvd-recovery", "DVD bytes were not exactly recovered")]
    [InlineData("cd-recovery", "CD source bytes were not exactly recovered")]
    [InlineData("subcode-raw-only-verification", "chdman did not verify both subcode fixture hashes")]
    [InlineData("subcode-verification-error-line", "chdman did not verify both subcode fixture hashes")]
    [InlineData("subcode-stored-bytes", "Stored frame/subcode bytes were not exactly recovered")]
    [InlineData("subcode-raw-sha1-header", "Independent subcode fixture hash mismatch")]
    [InlineData("subcode-overall-sha1-header", "Independent subcode fixture hash mismatch")]
    [InlineData("subcode-recovery", "TOC frame/subcode bytes were not exactly recovered")]
    public void EachToolMisbehaviorFailsItsCheck(string misbehavior, string message)
    {
        if (OperatingSystem.IsWindows())
            return;
        using var fake = new FakeChdman();
        const string RawOnly = "Raw SHA1 verification successful!\n";
        const string LaterError = FakeChdman.Verified + "Error: synthetic later failure\n";
        switch (misbehavior)
        {
            case "dvd-raw-only-verification":
                fake.Respond("dvd-lzma.chd", RawOnly);
                break;
            case "dvd-verification-error-line":
                fake.Respond("dvd-zstd.chd", LaterError);
                break;
            case "cd-silent-verification":
                fake.Respond("cd-cdfl.chd", "");
                break;
            case "uncompressed-verification-claims-success":
                fake.Respond("dvd-none.chd", FakeChdman.Verified);
                break;
            // The overall hash stays computed from the true raw hash, so only the raw comparison can fail.
            case "raw-sha1-header":
                fake.ChangeHeader("dvd-zlib", header => header with { RawSha1 = Flip(header.RawSha1) });
                break;
            case "overall-sha1-header":
                fake.ChangeHeader("cd-cdzl", header => header with { OverallSha1 = Flip(header.OverallSha1) });
                break;
            case "logical-length":
                fake.ChangeHeader("dvd-huff", header => header with { LogicalBytes = header.LogicalBytes + 1 });
                break;
            case "uncompressed-logical-length":
                fake.ChangeHeader("dvd-none", header => header with { LogicalBytes = header.LogicalBytes - 1 });
                break;
            case "dvd-recovery":
                fake.Replace("dvd-flac.extracted.iso", FakeChdman.Corrupt(SyntheticSources.Dvd()));
                break;
            case "cd-recovery":
                fake.Replace("cd-cdlz.extracted.bin", FakeChdman.Corrupt(SyntheticSources.Cd()));
                break;
            case "subcode-raw-only-verification":
                fake.Respond("cd-subcode.chd", RawOnly);
                break;
            case "subcode-verification-error-line":
                fake.Respond("cd-subcode.chd", LaterError);
                break;
            // The header agrees with the wrong logical bytes, so only the comparison with the source can fail.
            case "subcode-stored-bytes":
                fake.ChangeLogical("cd-subcode", FakeChdman.Corrupt(SyntheticSources.CdSubcode()));
                break;
            case "subcode-raw-sha1-header":
                fake.ChangeHeader("cd-subcode", header => header with { RawSha1 = Flip(header.RawSha1) });
                break;
            case "subcode-overall-sha1-header":
                fake.ChangeHeader("cd-subcode", header => header with { OverallSha1 = Flip(header.OverallSha1) });
                break;
            default:
                fake.Replace("cd-subcode.extracted.bin", FakeChdman.Corrupt(SyntheticSources.CdSubcode()));
                break;
        }
        Assert.Equal(message, fake.Run().Failure.Message);
    }

    /// <summary>Tool success cannot hide a changed edge projection, metadata, normalization, subcode or verification result.</summary>
    [Theory]
    [InlineData("projection", "Edge fixture source projection mismatch")]
    [InlineData("metadata", "Edge fixture metadata mismatch")]
    [InlineData("audio", "Edge CD source bytes were not exactly recovered")]
    [InlineData("subcode", "Edge CD source bytes were not exactly recovered")]
    [InlineData("length", "Edge subcode extraction length mismatch")]
    [InlineData("raw-hash", "Independent edge fixture hash mismatch")]
    [InlineData("verification", "chdman did not verify both edge fixture hashes")]
    public void RejectsChangedEdgeToolOutput(string change, string diagnostic)
    {
        if (OperatingSystem.IsWindows())
            return;
        using var fake = new FakeChdman();
        const string stem = "cd-edge-subcode";
        if (change == "projection")
            fake.ChangeLogical(stem, FakeChdman.Corrupt(CdEdgeFixtures.Logical(stem)));
        else if (change == "metadata")
            fake.ChangeHeader(stem, header => header with { TrackMetadata = ["wrong metadata"] });
        else if (change == "raw-hash")
            fake.ChangeHeader(stem, header => header with { RawSha1 = Flip(header.RawSha1) });
        else if (change == "verification")
            fake.Respond(stem + ".chd", "Raw SHA1 verification successful!\n");
        else
        {
            byte[] raw = File.ReadAllBytes(Path.Combine(fake.Prepared, stem + ".extracted.toc.bin"));
            if (change == "length")
                raw = raw[..^1];
            else
                raw[8 * 2448 + (change == "subcode" ? 2352 : 0)] ^= 1;
            fake.Replace(stem + ".extracted.toc.bin", raw);
        }
        Assert.Equal(diagnostic, fake.Run().Failure.Message);
    }

    /// <summary>Each negative case must report its own diagnostic; another case's rejection does not stand in for it.</summary>
    [Theory]
    [InlineData("raw-mismatch", "Error: Overall SHA1 in header = 0, actual = 1\n")]
    [InlineData("overall-mismatch", "Error: Raw SHA1 in header = 0, actual = 1\n")]
    [InlineData("raw-missing", "Error: Raw SHA1 in header = 0, actual = 1\n")]
    public void EachNegativeCaseRequiresItsOwnDiagnostic(string name, string response)
    {
        if (OperatingSystem.IsWindows())
            return;
        using var fake = new FakeChdman();
        fake.Respond("negative-" + name + ".chd", response);
        Assert.Equal("Negative verification was not rejected: " + name, fake.Run().Failure.Message);
    }

    /// <summary>A corrupted header that chdman reports as verified is accepted corruption and fails generation.</summary>
    [Theory]
    [InlineData("raw-mismatch")]
    [InlineData("overall-mismatch")]
    [InlineData("raw-missing")]
    public void NegativeCaseReportedAsVerifiedFails(string name)
    {
        if (OperatingSystem.IsWindows())
            return;
        using var fake = new FakeChdman();
        fake.Respond("negative-" + name + ".chd", FakeChdman.Verified);
        Assert.Equal("Negative verification was not rejected: " + name, fake.Run().Failure.Message);
    }

    private static string Flip(string sha1) => (sha1[0] == '0' ? "1" : "0") + sha1[1..];
}
