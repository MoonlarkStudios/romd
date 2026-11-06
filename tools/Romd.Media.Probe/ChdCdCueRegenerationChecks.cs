using System.Globalization;
using System.IO.Compression;
using System.IO.Hashing;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Xml;
using System.Xml.Linq;
using Romd.Infrastructure.Storage;
using Romd.Media.PlayStation;

namespace Romd.Media.Probe;

/// <summary>Offline byte comparison over a pinned, completed inspection receipt. Never publishes preservation evidence.</summary>
internal static class ChdCdCueRegenerationChecks
{
    private const int MaximumMetadataBytes = 32 << 20;
    private static readonly JsonSerializerOptions Strict = new()
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true,
        RespectNullableAnnotations = true,
        MaxDepth = 24
    };

    internal sealed record Configuration(string CorpusDirectory, string SourceDescription,
        string InspectionPath, string InspectionSha256, string DatPath, string DatSha256,
        string CueArchivePath, string CueArchiveSha256);
    internal sealed record Case(int Ordinal, string ArtifactSha256, int TrackCount, string Outcome, string? Reason,
        string? DatEntry, string? OriginalSha256, string? GeneratedSha256, string? LineEndings, bool? Utf8Bom, bool? QuotedFiles);
    internal sealed record Measurement(int Total, int ExactMatches, IReadOnlyList<Case> Cases);

    public static async Task RunAsync(string[] args, CancellationToken ct)
    {
        if (args.Length != 3)
            throw new ArgumentException("Usage: --chd-cd-cue-regeneration-probe <absolute configuration.json> <absolute new report.json>");
        MediaProbePaths.Validate([args[1], args[2]]);
        byte[] configuration = BoundedRuntimeFile.Read(args[1], 1 << 20);
        using var configDocument = JsonDocument.Parse(configuration, new JsonDocumentOptions { MaxDepth = 8 });
        Check.That(MediaRunRecord.Unique(configDocument.RootElement), "unique CUE measurement configuration fields");
        var config = JsonSerializer.Deserialize<Configuration>(configuration, Strict)
            ?? throw new InvalidDataException("Missing CUE measurement configuration.");
        ValidateConfiguration(config);
        MediaProbePaths.Validate([config.CorpusDirectory, args[1], args[2], config.InspectionPath, config.DatPath, config.CueArchivePath]);
        Check.That(!MediaProbePaths.Contains(config.CorpusDirectory, args[1]) &&
            !MediaProbePaths.Contains(config.CorpusDirectory, args[2]) && !File.Exists(args[2]) && !Directory.Exists(args[2]),
            "new measurement controls and output outside the read-only corpus");
        ct.ThrowIfCancellationRequested();
        byte[] inspection = ReadPinned(config.InspectionPath, config.InspectionSha256, 8 << 20);
        using var receipt = JsonDocument.Parse(inspection, new JsonDocumentOptions { MaxDepth = 24 });
        var observations = ReadReceipt(receipt.RootElement, config.CorpusDirectory);
        var measurement = Measure(observations, ReadPinned(config.DatPath, config.DatSha256, MaximumMetadataBytes),
            ReadPinned(config.CueArchivePath, config.CueArchiveSha256, 16 << 20));
        ct.ThrowIfCancellationRequested();
        await using var output = new FileStream(args[2], FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        await JsonSerializer.SerializeAsync(output, new
        {
            Schema = "romd-cue-regeneration-measurement/v1",
            Status = "measured",
            RecordedUtc = DateTimeOffset.UtcNow,
            Profile = ChdCdCueRegenerator.Profile,
            config.SourceDescription,
            ConfigurationSha256 = Digest(configuration),
            config.InspectionSha256,
            config.DatSha256,
            config.CueArchiveSha256,
            CapturedNative = receipt.RootElement.GetProperty("Native"),
            measurement.Total,
            measurement.ExactMatches,
            ExactMatchRate = (double)measurement.ExactMatches / measurement.Total,
            measurement.Cases,
            Scope = "Pinned completed supervised inspection receipt and independent DAT-authenticated CUE bytes. One BIN per measured track; no invented file grouping. Offline descriptor measurement only; no new native proof, import, publication, delivery, runtime or scheduling qualification."
        }, JsonOptions.Value, ct);
    }

    internal static void ValidateConfiguration(Configuration config)
    {
        Check.That(config.SourceDescription is { Length: > 0 and <= 4096 } && !config.SourceDescription.Any(char.IsControl),
            "explicit bounded metadata provenance");
        Check.That(Hash(config.InspectionSha256, 64) && Hash(config.DatSha256, 64) && Hash(config.CueArchiveSha256, 64),
            "exact nonzero inspection and independent metadata pins");
        MediaProbePaths.Validate([config.CorpusDirectory, config.InspectionPath, config.DatPath, config.CueArchivePath]);
    }

    internal static IReadOnlyList<ChdCdInspection> ReadReceipt(JsonElement root, string corpusDirectory)
    {
        Check.That(MediaRunRecord.Unique(root) && root.GetProperty("Status").GetString() == "passed" &&
            root.GetProperty("Error").ValueKind == JsonValueKind.Null, "completed successful inspection receipt");
        var native = root.GetProperty("Native").Deserialize<ChdNativeIdentity>(Strict)
            ?? throw new InvalidDataException("Missing captured native identity.");
        var rows = root.GetProperty("Rows").EnumerateArray().ToArray();
        Check.That(rows.Length is >= 2 and <= 17 && rows[0].GetProperty("Step").GetString() == "handshake" &&
            rows.Skip(1).All(row => row.GetProperty("Step").GetString() == "cd"), "one handshake and bounded CD measurements");
        var observed = new List<ChdCdInspection>();
        var artifacts = new HashSet<string>(StringComparer.Ordinal);
        var paths = new List<string>();
        foreach (var row in rows)
        {
            var result = row.GetProperty("Result");
            Check.That(result.GetProperty("IsSuccess").GetBoolean() && result.GetProperty("ProcessTreeStopped").GetBoolean() &&
                result.GetProperty("Failure").ValueKind == JsonValueKind.Null &&
                result.GetProperty("Reply").GetProperty("NativeIdentity").Deserialize<ChdNativeIdentity>(Strict) == native,
                "successful stopped measurements from the exact captured native build");
            if (row.GetProperty("Step").GetString() == "handshake")
                continue;
            string path = row.GetProperty("Input").GetProperty("ChdPath").GetString()
                ?? throw new InvalidDataException("Missing measured corpus path.");
            Check.That(MediaProbePaths.Contains(corpusDirectory, path), "every measured input belongs to the explicit read-only corpus");
            paths.Add(path);
            var facts = result.GetProperty("Reply").GetProperty("CdInspection").Deserialize<ChdCdInspection>(Strict)
                ?? throw new InvalidDataException("Missing complete CD observation.");
            Check.That(facts.Profile == ChdCdInspectionService.Profile && facts.ProjectionProfile == ChdCdInspectionService.ProjectionProfile &&
                Hash(facts.ArtifactSha256, 64) && facts.Tracks.Count is > 0 and <= ChdCdLimits.MaximumTracks,
                "recognized complete track measurement profile");
            Check.That(artifacts.Add(facts.ArtifactSha256), "distinct measured artifacts in the corpus denominator");
            observed.Add(facts);
        }
        MediaProbePaths.Validate(paths.ToArray());
        return observed;
    }

    internal static Measurement Measure(IReadOnlyList<ChdCdInspection> observations, byte[] dat, byte[] cueArchive)
    {
        Check.That(observations.Count is > 0 and <= 16 && dat.Length is > 0 and <= MaximumMetadataBytes &&
            cueArchive.Length is > 0 and <= (16 << 20), "bounded independent measurement inputs");
        using var xml = XmlReader.Create(new MemoryStream(dat, writable: false), new XmlReaderSettings
        { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null, MaxCharactersInDocument = MaximumMetadataBytes, CloseInput = true });
        var document = XDocument.Load(xml);
        var root = document.Root ?? throw new InvalidDataException("Missing DAT document root.");
        Check.That(root.Name == "datafile" && root.Element("header")?.Element("name")?.Value == "Sony - PlayStation",
            "explicit standard PlayStation DAT identity");
        var games = root.Elements("game").Take(20001).ToArray();
        Check.That(games.Length is > 0 and <= 20000 && games.All(game => game.Elements("rom").Take(117).Count() is > 0 and <= 116),
            "bounded complete DAT entries");
        using var archive = new ZipArchive(new MemoryStream(cueArchive, writable: false), ZipArchiveMode.Read);
        Check.That(archive.Entries.Count is > 0 and <= 20000 && archive.Entries.Sum(entry => entry.Length) <= MaximumMetadataBytes,
            "bounded CUE archive metadata");
        var cases = new List<Case>();
        foreach (var facts in observations)
        {
            var candidates = games.Select(game => Bind(game, facts.Tracks)).OfType<Binding>().ToArray();
            if (candidates.Length != 1)
            {
                cases.Add(Failure(cases.Count + 1, facts, "Unresolved", candidates.Length == 0 ? "NoTrackFileDatMatch" : "AmbiguousDatMatch"));
                continue;
            }
            var selected = candidates[0];
            string name = selected.Game.Attribute("name")?.Value ?? throw new InvalidDataException("Missing DAT entry name.");
            var others = selected.Game.Elements("rom").Where(rom => !selected.Bins.Contains(rom)).ToArray();
            string? cueName = others.Length == 1 ? (string?)others[0].Attribute("name") : null;
            if (cueName is null || !cueName.EndsWith(".cue", StringComparison.OrdinalIgnoreCase))
            {
                cases.Add(Failure(cases.Count + 1, facts, "Unsupported", "AdditionalOrMissingDescriptorRequirements"));
                continue;
            }
            var cue = others[0];
            Check.That(ChdCdCue.PortableName(cueName), "exact portable catalog CUE name");
            var members = archive.Entries.Where(entry => entry.FullName == cueName).ToArray();
            Check.That(members.Length == 1 && members[0].Length is > 0 and <= ChdCdCue.MaximumBytes, "unique bounded independent CUE oracle");
            byte[] original = new byte[checked((int)members[0].Length)];
            using (var stream = members[0].Open())
            {
                stream.ReadExactly(original);
                Check.That(stream.ReadByte() == -1, "exact independent CUE archive member length");
            }
            Check.That(Matches(cue, original), "original CUE bytes authenticate against all DAT digests and size");
            byte[] generated;
            try
            {
                generated = ChdCdCueRegenerator.Regenerate(selected.Bins.Select((rom, i) =>
                    new ChdCdCueTrackFile(rom.Attribute("name")?.Value ?? throw new InvalidDataException("Missing bound BIN name."), facts.Tracks[i])).ToArray());
            }
            catch (Exception error) when (error is InvalidDataException or NotSupportedException)
            {
                cases.Add(Failure(cases.Count + 1, facts, "Unsupported", "TrackGeometryOrName"));
                continue;
            }
            bool exact = generated.AsSpan().SequenceEqual(original);
            string text = new UTF8Encoding(false, true).GetString(original);
            string endings = text.Contains("\r\n", StringComparison.Ordinal)
                ? text.Replace("\r\n", "", StringComparison.Ordinal).Contains('\n') ? "Mixed" : "CRLF"
                : text.Contains('\n') ? "LF" : "None";
            bool bom = original.AsSpan().StartsWith(new byte[] { 0xef, 0xbb, 0xbf });
            var fileLines = text.TrimStart('\uFEFF').Split('\n').Where(line => line.TrimStart().StartsWith("FILE ", StringComparison.Ordinal)).ToArray();
            bool quoted = fileLines.Length != 0 && fileLines.All(line => line.Trim().StartsWith("FILE \"", StringComparison.Ordinal) && line.Trim().EndsWith("\" BINARY", StringComparison.Ordinal));
            string generatedText = Encoding.UTF8.GetString(generated);
            string? reason = exact ? null
                : bom && text[1..] == generatedText ? "Utf8Bom"
                : NormalizeLines(text) == NormalizeLines(generatedText) ? "LineEndings"
                : bom && NormalizeLines(text[1..]) == NormalizeLines(generatedText) ? "Utf8BomAndLineEndings"
                : "DescriptorBytes";
            cases.Add(new(cases.Count + 1, facts.ArtifactSha256, facts.Tracks.Count, exact ? "ExactMatch" : "Mismatch", reason,
                name, Digest(original), Digest(generated), endings, bom, quoted));
        }
        return new(cases.Count, cases.Count(item => item.Outcome == "ExactMatch"), cases.AsReadOnly());
    }

    private sealed record Binding(XElement Game, XElement[] Bins);
    private static Binding? Bind(XElement game, IReadOnlyList<ChdCdTrackInspection> tracks)
    {
        var bins = game.Elements("rom").Where(rom => ((string?)rom.Attribute("name"))?.EndsWith(".bin", StringComparison.OrdinalIgnoreCase) == true).ToArray();
        if (bins.Length != tracks.Count)
            return null;
        var selected = new List<XElement>();
        foreach (var track in tracks)
        {
            var matches = bins.Where(rom => Size(rom) == track.BinBytes &&
                EqualHash(rom, "sha1", track.Sha1) && EqualHash(rom, "md5", track.Md5) && EqualHash(rom, "crc", track.Crc32)).ToArray();
            if (matches.Length != 1 || selected.Contains(matches[0]))
                return null;
            selected.Add(matches[0]);
        }
        return new(game, selected.ToArray());
    }
    private static bool Matches(XElement rom, byte[] bytes) => Size(rom) == bytes.LongLength &&
        EqualHash(rom, "sha1", Convert.ToHexStringLower(SHA1.HashData(bytes))) &&
        EqualHash(rom, "md5", Convert.ToHexStringLower(MD5.HashData(bytes))) &&
        EqualHash(rom, "crc", Crc32.HashToUInt32(bytes).ToString("x8", CultureInfo.InvariantCulture));
    private static bool EqualHash(XElement rom, string name, string value) => string.Equals((string?)rom.Attribute(name), value, StringComparison.OrdinalIgnoreCase);
    private static string NormalizeLines(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal);
    private static long Size(XElement rom) => long.TryParse((string?)rom.Attribute("size"), NumberStyles.None, CultureInfo.InvariantCulture, out long size) ? size : -1;
    private static Case Failure(int ordinal, ChdCdInspection facts, string outcome, string reason) =>
        new(ordinal, facts.ArtifactSha256, facts.Tracks.Count, outcome, reason, null, null, null, null, null, null);
    private static bool Hash(string? hash, int length) => hash?.Length == length && hash.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f') && hash.Any(c => c != '0');
    private static string Digest(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    private static byte[] ReadPinned(string path, string sha256, int maximum)
    {
        byte[] bytes = BoundedRuntimeFile.Read(path, maximum);
        Check.That(Digest(bytes) == sha256, "pinned immutable qualification input bytes");
        return bytes;
    }
}
