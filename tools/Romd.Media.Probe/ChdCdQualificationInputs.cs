using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Xml;
using System.Xml.Linq;
using Romd.Infrastructure.Storage;
using Romd.Media.Cd;
using Romd.Media.Processes;

namespace Romd.Media.Probe;

/// <summary>Qualification inputs are independent oracles, never production admission evidence.</summary>
internal static class ChdCdQualificationInputs
{
    internal sealed record Configuration(string SourceDescription, string FixtureKind, string NativeDirectory,
        string ToolDirectory, string DataDirectory, Input[] Inputs);
    internal sealed record Input(string Name, string FixtureDirectory, string CueName, string[] Sidecars,
        string DatPath, string DatSha256, string DatEntryName, long MaximumOutputBytes,
        IReadOnlyDictionary<string, string>? DatBinNames = null, string? CatalogCueName = null);
    internal sealed record DatMember(long Size, string Crc, string Md5, string Sha1);
    internal sealed record CatalogCue(string Name, DatMember Identity);
    internal sealed record Layout(Input Input, ChdCdCue Cue, IReadOnlyDictionary<string, DatMember> Dat,
        IReadOnlyList<string> Names, IReadOnlyDictionary<string, string> DatNames, CatalogCue? CatalogCueReference);

    internal static Configuration Parse(byte[] bytes)
    {
        Check.That(bytes.Length is > 0 and <= (1 << 20), "bounded CD qualification configuration");
        using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 8 });
        Check.That(MediaRunRecord.Unique(document.RootElement), "unique CD qualification fields");
        var config = JsonSerializer.Deserialize<Configuration>(bytes, new JsonSerializerOptions
        {
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            RespectRequiredConstructorParameters = true,
            RespectNullableAnnotations = true,
            MaxDepth = 8
        }) ?? throw new InvalidDataException("Missing qualification configuration.");
        Validate(config);
        return config;
    }

    internal static void Validate(Configuration config)
    {
        Check.That(config.SourceDescription is { Length: > 0 and <= 4096 } && !config.SourceDescription.Any(char.IsControl) &&
            config.FixtureKind is "real" or "synthetic" && config.Inputs is { Length: > 0 and <= 16 }, "explicit bounded qualification scope");
        HashSet<string> names = new(StringComparer.OrdinalIgnoreCase);
        foreach (var input in config.Inputs)
        {
            if (input is null)
                throw new InvalidOperationException("A qualification input is required.");
            Check.That(ChdCdCue.PortableName(input.Name) && names.Add(input.Name) &&
                ChdCdCue.PortableName(input.CueName) && input.Sidecars is { Length: <= 16 } &&
                input.Sidecars.All(ChdCdCue.PortableName) && input.Sidecars.Append(input.CueName).Distinct(StringComparer.OrdinalIgnoreCase).Count() == input.Sidecars.Length + 1 &&
                ChdCdJson.Hash(input.DatSha256, 64) && input.DatEntryName is { Length: > 0 and <= 512 } &&
                !input.DatEntryName.Any(char.IsControl) && input.MaximumOutputBytes is >= 124 and <= ChdCdLimits.MaximumArtifactBytes,
                "complete distinct qualification input");
            if (input.DatBinNames is { } bindings)
                Check.That(bindings.Count is > 0 and <= 99 && bindings.All(pair => ChdCdCue.PortableName(pair.Key) && ChdCdCue.PortableName(pair.Value)) &&
                    bindings.Keys.Distinct(StringComparer.OrdinalIgnoreCase).Count() == bindings.Count &&
                    bindings.Values.Distinct(StringComparer.OrdinalIgnoreCase).Count() == bindings.Count,
                    "explicit bounded one-to-one DAT BIN bindings");
            if (input.CatalogCueName is { } catalogCue)
                Check.That(ChdCdCue.PortableName(catalogCue) && catalogCue.EndsWith(".cue", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(catalogCue, input.CueName, StringComparison.OrdinalIgnoreCase),
                    "catalog descriptor reference is a distinct CUE, never an unchecked BIN or original descriptor");
        }
    }

    internal static void ValidatePaths(Configuration config, string configuration, string report)
    {
        string[] protectedPaths = [configuration, config.NativeDirectory, config.ToolDirectory, AppContext.BaseDirectory,
            .. config.Inputs.SelectMany(input => new[] { input.FixtureDirectory, input.DatPath })];
        MediaProbePaths.Validate([.. protectedPaths.Distinct(StringComparer.Ordinal), config.DataDirectory, report]);
        Check.That(!Directory.Exists(config.DataDirectory) && !File.Exists(config.DataDirectory) &&
            !Directory.Exists(report) && !File.Exists(report), "fresh owned qualification data and report");
        Check.That(!MediaProbePaths.Overlaps(config.DataDirectory, report) &&
            !protectedPaths.Any(path => MediaProbePaths.Overlaps(path, config.DataDirectory) || MediaProbePaths.Overlaps(path, report)),
            "qualification outputs outside inputs and executable directories");
    }

    internal static Layout ReadLayout(Input input)
    {
        string cuePath = Path.Combine(input.FixtureDirectory, input.CueName);
        MediaProbePaths.Validate([cuePath]);
        var cue = ChdCdCue.Parse(BoundedRuntimeFile.Read(cuePath, ChdCdCue.MaximumBytes));
        string[] names = [input.CueName, .. cue.Files.Select(file => file.Name), .. input.Sidecars];
        Check.That(names.Distinct(StringComparer.OrdinalIgnoreCase).Count() == names.Length, "unique original file names");
        MediaProbePaths.Validate(names.Select(name => Path.Combine(input.FixtureDirectory, name)).ToArray());
        using var dat = new FileStream(NativeFileLease.OpenRegularRead(input.DatPath), FileAccess.Read);
        Check.That(dat.Length is > 0 and <= (64 << 20), "bounded independent DAT");
        byte[] datBytes = new byte[checked((int)dat.Length)];
        dat.ReadExactly(datBytes);
        Check.That(dat.ReadByte() == -1 && Convert.ToHexStringLower(SHA256.HashData(datBytes)) == input.DatSha256,
            "pinned bounded independent DAT snapshot");
        var members = ReadDat(new MemoryStream(datBytes, writable: false), input.DatEntryName);
        var datNames = input.DatBinNames is { } supplied
            ? new Dictionary<string, string>(supplied, StringComparer.Ordinal)
            : cue.Files.ToDictionary(file => file.Name, file => file.Name, StringComparer.Ordinal);
        Check.That(datNames.Keys.Order(StringComparer.Ordinal).SequenceEqual(cue.Files.Select(file => file.Name).Order(StringComparer.Ordinal)) &&
            datNames.Values.Distinct(StringComparer.OrdinalIgnoreCase).Count() == datNames.Count &&
            datNames.Values.All(members.ContainsKey), "complete explicit BIN binding to unique DAT members");
        var bound = datNames.ToDictionary(pair => pair.Key, pair => members[pair.Value], StringComparer.Ordinal);
        foreach (var file in cue.Files)
            Check.That(bound[file.Name].Size % 2352 == 0, "whole raw BIN sectors in the DAT");
        HashSet<string> covered = new(datNames.Values, StringComparer.Ordinal);
        foreach (string name in input.Sidecars.Prepend(input.CueName))
        {
            if (!members.TryGetValue(name, out var identity))
                continue;
            Check.That(covered.Add(name), "a DAT member has exactly one original-file role");
            Check.That(identity.Size <= (name == input.CueName ? ChdCdCue.MaximumBytes : 16 << 20), "bounded DAT descriptor or sidecar");
            bound.Add(name, identity);
            datNames.Add(name, name);
        }
        CatalogCue? reference = null;
        if (input.CatalogCueName is { } catalogName)
        {
            Check.That(ChdCdCue.PortableName(catalogName) && catalogName.EndsWith(".cue", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(catalogName, input.CueName, StringComparison.OrdinalIgnoreCase) &&
                members.TryGetValue(catalogName, out var identity) && identity.Size <= ChdCdCue.MaximumBytes && covered.Add(catalogName),
                "explicit catalog CUE reference cannot hide an original file or BIN");
            reference = new(catalogName, members[catalogName]);
        }
        Check.That(covered.SetEquals(members.Keys), "every DAT member is matched or is the explicit catalog CUE reference");
        Check.That(cue.Files.Sum(file => bound[file.Name].Size / 2352) <= ChdCdLimits.MaximumFrames, "bounded total source frames");
        return new(input, cue, bound, names, datNames, reference);
    }

    internal static IReadOnlyDictionary<string, DatMember> ReadDat(Stream source, string entryName)
    {
        using var reader = XmlReader.Create(source, new XmlReaderSettings
        { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null, MaxCharactersInDocument = 64 << 20, CloseInput = false });
        var entries = XDocument.Load(reader).Descendants("game").Where(game => (string?)game.Attribute("name") == entryName).ToArray();
        Check.That(entries.Length == 1, "exact unique DAT game entry");
        var roms = entries[0].Elements("rom").ToArray();
        Check.That(roms.Length is > 0 and <= 116, "complete bounded DAT member list");
        Dictionary<string, DatMember> members = new(StringComparer.Ordinal);
        HashSet<string> names = new(StringComparer.OrdinalIgnoreCase);
        foreach (var rom in roms)
        {
            string? name = (string?)rom.Attribute("name"), crc = (string?)rom.Attribute("crc"),
                md5 = (string?)rom.Attribute("md5"), sha1 = (string?)rom.Attribute("sha1");
            Check.That(ChdCdCue.PortableName(name) && names.Add(name!) &&
                long.TryParse((string?)rom.Attribute("size"), NumberStyles.None, CultureInfo.InvariantCulture, out _) &&
                ChdCdJson.Hash(crc, 8) && ChdCdJson.Hash(md5, 32) && ChdCdJson.Hash(sha1, 40) &&
                (string?)rom.Attribute("status") != "nodump", "complete unambiguous DAT file identity");
            long size = long.Parse((string)rom.Attribute("size")!, CultureInfo.InvariantCulture);
            Check.That(size is > 0 and <= 1058400000, "bounded DAT member size");
            members.Add(name!, new(size, crc!, md5!, sha1!));
        }
        return members;
    }

    internal static void MatchDat(Digests actual, DatMember expected) => Check.That(actual.Size == expected.Size &&
        actual.Crc == expected.Crc && actual.Md5 == expected.Md5 && actual.Sha1 == expected.Sha1, "independent DAT size and all three digests");
}
