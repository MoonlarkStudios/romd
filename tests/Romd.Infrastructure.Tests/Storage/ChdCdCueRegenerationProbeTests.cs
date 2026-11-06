using System.Globalization;
using System.IO.Compression;
using System.IO.Hashing;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using Moonlark.Libchdr;
using Romd.Media.PlayStation;
using Romd.Media.Probe;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Storage;

public sealed class ChdCdCueRegenerationProbeTests
{
    [Theory]
    [InlineData("CRLF", false, "ExactMatch", null)]
    [InlineData("LF", false, "Mismatch", "LineEndings")]
    [InlineData("CRLF", true, "Mismatch", "Utf8Bom")]
    [InlineData("LF", true, "Mismatch", "Utf8BomAndLineEndings")]
    public void Measure_AuthenticatedIndependentCue_ReportsExactBytesAndObservedStyle(string endings, bool bom, string outcome, string? reason)
    {
        var fixture = Inputs(endings, bom);

        var measured = ChdCdCueRegenerationChecks.Measure([fixture.Facts], fixture.Dat, fixture.Zip);

        measured.Total.ShouldBe(1);
        measured.ExactMatches.ShouldBe(outcome == "ExactMatch" ? 1 : 0);
        var item = measured.Cases.Single();
        item.Outcome.ShouldBe(outcome);
        item.Reason.ShouldBe(reason);
        item.DatEntry.ShouldBe("Independent Disc");
        item.LineEndings.ShouldBe(endings);
        item.Utf8Bom.ShouldBe(bom);
        item.QuotedFiles.ShouldBe(true);
        item.OriginalSha256.ShouldBe(Hash(fixture.Cue, SHA256.HashData));
        item.GeneratedSha256.ShouldNotBeNull();
    }

    [Theory]
    [InlineData("duplicate-entry", "Unresolved", "AmbiguousDatMatch")]
    [InlineData("wrong-track-digest", "Unresolved", "NoTrackFileDatMatch")]
    [InlineData("additional-sidecar", "Unsupported", "AdditionalOrMissingDescriptorRequirements")]
    public void Measure_UnsupportedOrAmbiguousRequirements_RemainInDenominatorWithoutOriginalClaim(string mutation, string outcome, string reason)
    {
        var fixture = Inputs("CRLF", false);
        var doc = XDocument.Parse(Encoding.UTF8.GetString(fixture.Dat));
        var game = doc.Root!.Element("game")!;
        if (mutation == "duplicate-entry")
            doc.Root.Add(new XElement(game));
        else if (mutation == "wrong-track-digest")
            game.Elements("rom").First().SetAttributeValue("sha1", new string('0', 40));
        else
            game.Add(Rom("disc.sbi", [0x53, 0x42, 0x49, 0]));

        var measured = ChdCdCueRegenerationChecks.Measure([fixture.Facts], Encoding.UTF8.GetBytes(doc.ToString()), fixture.Zip);

        measured.Total.ShouldBe(1);
        measured.ExactMatches.ShouldBe(0);
        var item = measured.Cases.Single();
        item.Outcome.ShouldBe(outcome);
        item.Reason.ShouldBe(reason);
        item.OriginalSha256.ShouldBeNull();
        item.GeneratedSha256.ShouldBeNull();
    }

    [Fact]
    public void Measure_CueArchiveDisagreesWithDat_RejectsUnauthenticatedOracle()
    {
        var fixture = Inputs("CRLF", false);
        byte[] forged = Zip("Independent Disc.cue", [.. fixture.Cue, (byte)'x']);

        Should.Throw<InvalidOperationException>(() => ChdCdCueRegenerationChecks.Measure([fixture.Facts], fixture.Dat, forged))
            .Message.ShouldContain("authenticate against all DAT digests and size");
    }

    [Fact]
    public void Measure_DifferentAuthenticatedDescriptorTextAndLineEndings_DoesNotMisattributeContentMismatch()
    {
        var fixture = Inputs("LF", false);
        byte[] original = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(fixture.Cue).Replace("MODE2/2352", "MODE1/2352", StringComparison.Ordinal));
        var dat = XDocument.Parse(Encoding.UTF8.GetString(fixture.Dat));
        dat.Root!.Element("game")!.Elements("rom").Last().ReplaceWith(Rom("Independent Disc.cue", original));

        var measured = ChdCdCueRegenerationChecks.Measure([fixture.Facts], Encoding.UTF8.GetBytes(dat.ToString()), Zip("Independent Disc.cue", original));

        measured.Total.ShouldBe(1);
        measured.ExactMatches.ShouldBe(0);
        measured.Cases.Single().Reason.ShouldBe("DescriptorBytes");
        measured.Cases.Single().LineEndings.ShouldBe("LF");
    }

    [Fact]
    public void Measure_UppercaseDatDigestSpelling_StillRequiresTheSameFullIdentity()
    {
        var fixture = Inputs("CRLF", false);
        var dat = XDocument.Parse(Encoding.UTF8.GetString(fixture.Dat));
        foreach (var rom in dat.Descendants("rom"))
            foreach (string name in new[] { "sha1", "md5", "crc" })
                rom.SetAttributeValue(name, rom.Attribute(name)!.Value.ToUpperInvariant());

        var measured = ChdCdCueRegenerationChecks.Measure([fixture.Facts], Encoding.UTF8.GetBytes(dat.ToString()), fixture.Zip);

        measured.Total.ShouldBe(1);
        measured.ExactMatches.ShouldBe(1);
    }

    [Theory]
    [InlineData("not-complete")]
    [InlineData("not-stopped")]
    [InlineData("different-decoder")]
    [InlineData("missing-handshake")]
    [InlineData("foreign-corpus")]
    [InlineData("different-profile")]
    [InlineData("failure-present")]
    [InlineData("duplicate-artifact")]
    public void ReadReceipt_ContradictoryCustodyOrScope_Rejects(string mutation)
    {
        var fixture = Inputs("CRLF", false);
        var root = Receipt(fixture.Facts);
        var result = root["Rows"]![1]!["Result"]!;
        switch (mutation)
        {
            case "not-complete":
                root["Status"] = "in-progress";
                break;
            case "not-stopped":
                result["ProcessTreeStopped"] = false;
                break;
            case "different-decoder":
                result["Reply"]!["NativeIdentity"]!["RecipeVersion"] = 3;
                break;
            case "missing-handshake":
                ((JsonArray)root["Rows"]!).RemoveAt(0);
                break;
            case "foreign-corpus":
                root["Rows"]![1]!["Input"]!["ChdPath"] = "/elsewhere/disc.chd";
                break;
            case "different-profile":
                result["Reply"]!["CdInspection"]!["Profile"] = "unrecognized";
                break;
            case "failure-present":
                result["Failure"] = "Crash";
                break;
            case "duplicate-artifact":
                var copy = root["Rows"]![1]!.DeepClone();
                copy["Input"]!["ChdPath"] = "/corpus/another.chd";
                ((JsonArray)root["Rows"]!).Add(copy);
                break;
        }
        using var document = JsonDocument.Parse(root.ToJsonString());

        Should.Throw<InvalidOperationException>(() => ChdCdCueRegenerationChecks.ReadReceipt(document.RootElement, "/corpus"));
    }

    [Fact]
    public void ReadReceipt_StoppedCompleteCapturedBuild_PreservesMeasuredTrackFacts()
    {
        var fixture = Inputs("CRLF", false);
        using var document = JsonDocument.Parse(Receipt(fixture.Facts).ToJsonString());

        var facts = ChdCdCueRegenerationChecks.ReadReceipt(document.RootElement, "/corpus").Single();

        facts.ArtifactSha256.ShouldBe(fixture.Facts.ArtifactSha256);
        facts.Tracks.ShouldBe(fixture.Facts.Tracks);
    }

    [Theory]
    [InlineData("missing-pin")]
    [InlineData("zero-pin")]
    [InlineData("relative-root")]
    [InlineData("reused-input-path")]
    [InlineData("empty-provenance")]
    public void ValidateConfiguration_ImplicitOrUnpinnedInputs_Rejects(string mutation)
    {
        var config = new ChdCdCueRegenerationChecks.Configuration("/corpus", "Independent metadata",
            "/inspection.json", new string('a', 64), "/catalog.dat", new string('b', 64), "/cues.zip", new string('c', 64));
        config = mutation switch
        {
            "missing-pin" => config with { DatSha256 = "" },
            "zero-pin" => config with { CueArchiveSha256 = new string('0', 64) },
            "relative-root" => config with { CorpusDirectory = "corpus" },
            "reused-input-path" => config with { DatPath = config.InspectionPath },
            "empty-provenance" => config with { SourceDescription = "" },
            _ => throw new ArgumentException(mutation)
        };
        Should.Throw<InvalidOperationException>(() => ChdCdCueRegenerationChecks.ValidateConfiguration(config));
    }

    private sealed record Fixture(ChdCdInspection Facts, byte[] Dat, byte[] Zip, byte[] Cue);
    private static Fixture Inputs(string endings, bool bom)
    {
        byte[] bin = Enumerable.Range(0, 4 * 2352).Select(i => (byte)(i * 13)).ToArray();
        var track = new ChdCdTrackInspection(1, ChdCdTrackType.Mode2Raw, 0, 4, 0, 0, false, 0,
            bin.Length, Hash(bin, SHA256.HashData), Hash(bin, SHA1.HashData), Hash(bin, MD5.HashData), Crc32.HashToUInt32(bin).ToString("x8", CultureInfo.InvariantCulture));
        var facts = new ChdCdInspection(ChdCdInspectionService.Profile, ChdCdInspectionService.ProjectionProfile, new string('d', 64),
            5, false, new(124, 4 * 2448, 4 * 2448, 2448, 1, 0, 0, 0, 0), new string('e', 40), new string('e', 40),
            new string('f', 40), new string('f', 40), [track], [], null);
        string eol = endings == "CRLF" ? "\r\n" : "\n";
        byte[] content = Encoding.UTF8.GetBytes($"FILE \"Independent Disc.bin\" BINARY{eol}  TRACK 01 MODE2/2352{eol}    INDEX 01 00:00:00{eol}");
        byte[] cue = bom ? [0xef, 0xbb, 0xbf, .. content] : content;
        var dat = new XDocument(new XElement("datafile", new XElement("header", new XElement("name", "Sony - PlayStation")),
            new XElement("game", new XAttribute("name", "Independent Disc"), Rom("Independent Disc.bin", bin), Rom("Independent Disc.cue", cue))));
        return new(facts, Encoding.UTF8.GetBytes(dat.ToString()), Zip("Independent Disc.cue", cue), cue);
    }
    private static XElement Rom(string name, byte[] bytes) => new("rom", new XAttribute("name", name), new XAttribute("size", bytes.Length),
        new XAttribute("sha1", Hash(bytes, SHA1.HashData)), new XAttribute("md5", Hash(bytes, MD5.HashData)),
        new XAttribute("crc", Crc32.HashToUInt32(bytes).ToString("x8", CultureInfo.InvariantCulture)));
    private static string Hash(byte[] bytes, Func<byte[], byte[]> digest) => Convert.ToHexStringLower(digest(bytes));
    private static byte[] Zip(string name, byte[] bytes)
    {
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        using (var stream = zip.CreateEntry(name).Open())
            stream.Write(bytes);
        return buffer.ToArray();
    }
    private static JsonObject Receipt(ChdCdInspection facts)
    {
        var native = new ChdNativeIdentity("linux-x64", "fixture.so", 1, new string('a', 64), new string('b', 40), "fixture", 2,
            "fixture", new string('c', 64), true, true, true);
        object Result(ChdCdInspection? observation) => new
        { IsSuccess = true, ProcessTreeStopped = true, Failure = (string?)null, Reply = new { NativeIdentity = native, CdInspection = observation } };
        return JsonSerializer.SerializeToNode(new
        {
            Status = "passed",
            Error = (string?)null,
            Native = native,
            Rows = new object[] { new { Step = "handshake", Result = Result(null) },
                new { Step = "cd", Input = new { ChdPath = "/corpus/disc.chd" }, Result = Result(facts) } }
        })!.AsObject();
    }
}
