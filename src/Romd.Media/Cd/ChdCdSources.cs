using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Moonlark.Libchdr;

namespace Romd.Media.Cd;

public sealed record ChdCdSourceFile(string Name, string Path, long Bytes, string Sha256, string Role);
public sealed record ChdCdRequest(string Profile, string ToolCuePath, IReadOnlyList<ChdCdSourceFile> Files, string? RequiredBootProfile = null)
{
    public const string CurrentProfile = "chd-cd-source-set/v1";
    public const int MaximumBytes = 512 << 10;

    public void Validate()
    {
        if (Profile != CurrentProfile || !CdBootObservation.Supports(RequiredBootProfile) || !ChdCdJson.Absolute(ToolCuePath) || Files is null || Files.Count is < 2 or > 116 ||
            Files.Any(f => f is null || !ChdCdCue.PortableName(f.Name) || !ChdCdJson.Absolute(f.Path) ||
                f.Path == ToolCuePath || !ChdCdJson.Hash(f.Sha256, 64) || f.Bytes < 1 || f.Role switch
                {
                    "bin" => f.Bytes > 1058400000 || f.Bytes % 2352 != 0,
                    "cue" => f.Bytes > ChdCdCue.MaximumBytes,
                    "sidecar" => f.Bytes > (16 << 20),
                    _ => true
                }) ||
            Files.Count(f => f.Role == "cue") != 1 || Files.Count(f => f.Role == "bin") is < 1 or > 99 ||
            Files.Count(f => f.Role == "sidecar") > 16 || Files.Where(f => f.Role == "bin").Sum(f => f.Bytes / 2352) > 450000 ||
            Files.Select(f => f.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != Files.Count ||
            Files.Select(f => f.Path).Distinct(StringComparer.Ordinal).Count() != Files.Count)
            throw new InvalidDataException("Unsupported or incomplete CD source set.");
    }
}

public sealed record ChdCdCueFile(string Name, IReadOnlyList<int> Tracks);

/// <summary>Bounded raw BINARY CUE subset. FILE references are names, never host paths. Original text is retained separately.</summary>
public sealed class ChdCdCue
{
    public const int MaximumBytes = 64 << 10;
    private readonly string[] _lines;
    private readonly Dictionary<int, string> _fileLines;
    public IReadOnlyList<ChdCdCueFile> Files { get; }
    public IReadOnlyList<ChdCdTrackType> TrackTypes { get; }
    public long FirstTrackIndex01 { get; }
    private ChdCdCue(string[] lines, Dictionary<int, string> fileLines, IReadOnlyList<ChdCdCueFile> files, IReadOnlyList<ChdCdTrackType> types, long firstTrackIndex01)
    { _lines = lines; _fileLines = fileLines; Files = files; TrackTypes = types; FirstTrackIndex01 = firstTrackIndex01; }

    public static ChdCdCue Parse(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length is < 1 or > MaximumBytes)
            throw new InvalidDataException("CUE exceeds its byte budget.");
        string text = new UTF8Encoding(false, true).GetString(bytes).TrimStart('\uFEFF');
        if (text.Any(c => char.IsControl(c) && c is not ('\r' or '\n' or '\t')))
            throw new InvalidDataException("Invalid CUE control character.");
        string[] lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        Dictionary<int, string> fileLines = [];
        List<(string Name, List<int> Tracks)> files = [];
        List<ChdCdTrackType> types = [];
        long firstTrackIndex01 = 0;
        int track = 0;
        bool indexed = true;
        int previousIndex = -1;
        long previousTime = -1;
        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i].Trim();
            if (line.Length == 0 || line.StartsWith("REM ", StringComparison.Ordinal))
                continue;
            var file = Regex.Match(line, "^FILE \"([^\"]{1,240})\" BINARY$", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
            if (file.Success)
            {
                string name = file.Groups[1].Value;
                if (!indexed || !PortableName(name) || files.Count >= 99 || files.Any(f => string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase)) ||
                    files.Count > 0 && files[^1].Tracks.Count == 0)
                    throw new InvalidDataException("Invalid CUE file reference.");
                files.Add((name, []));
                fileLines.Add(i, name);
                previousTime = -1;
                continue;
            }
            var next = Regex.Match(line, "^TRACK ([0-9]{2}) (MODE1/2352|MODE2/2352|AUDIO)$", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
            if (next.Success)
            {
                if (!indexed || files.Count == 0 || ++track > 99 || int.Parse(next.Groups[1].Value, CultureInfo.InvariantCulture) != track)
                    throw new InvalidDataException("CUE tracks must be consecutive raw tracks with INDEX 01.");
                files[^1].Tracks.Add(track);
                types.Add(next.Groups[2].Value switch { "MODE1/2352" => ChdCdTrackType.Mode1Raw, "MODE2/2352" => ChdCdTrackType.Mode2Raw, _ => ChdCdTrackType.Audio });
                indexed = false;
                previousIndex = -1;
                continue;
            }
            var timing = Regex.Match(line, "^(INDEX (00|01)|PREGAP|POSTGAP) ([0-9]{2}):([0-9]{2}):([0-9]{2})$", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
            if (timing.Success)
            {
                int minutes = int.Parse(timing.Groups[3].Value, CultureInfo.InvariantCulture), seconds = int.Parse(timing.Groups[4].Value, CultureInfo.InvariantCulture),
                    frames = int.Parse(timing.Groups[5].Value, CultureInfo.InvariantCulture);
                long time = (minutes * 60L + seconds) * 75 + frames;
                if (track == 0 || seconds >= 60 || frames >= 75 || time > 450000)
                    throw new InvalidDataException("Invalid CUE time.");
                if (timing.Groups[2].Success)
                {
                    int index = int.Parse(timing.Groups[2].Value, CultureInfo.InvariantCulture);
                    if (index <= previousIndex || time < previousTime)
                        throw new InvalidDataException("Unordered CUE indexes.");
                    previousIndex = index;
                    previousTime = time;
                    if (index == 1)
                    { indexed = true; if (track == 1) firstTrackIndex01 = time; }
                }
                else if (timing.Groups[1].Value == "PREGAP" ? indexed || previousIndex >= 0 : !indexed)
                    throw new InvalidDataException("Invalid CUE gap placement.");
                continue;
            }
            throw new InvalidDataException("Unsupported CUE directive; only raw BINARY tracks, indexes, gaps and REM are accepted.");
        }
        if (!indexed || track == 0 || files.Any(f => f.Tracks.Count == 0))
            throw new InvalidDataException("Incomplete CUE.");
        return new(lines, fileLines, files.Select(f => new ChdCdCueFile(f.Name, f.Tracks.AsReadOnly())).ToArray(), types.AsReadOnly(), firstTrackIndex01);
    }

    public byte[] RenderToolCue(IReadOnlyDictionary<string, string> paths, string toolCuePath)
    {
        if (!ChdCdJson.Absolute(toolCuePath) || paths.Count != Files.Count || Files.Any(f => !paths.TryGetValue(f.Name, out string? path) || !ChdCdJson.Absolute(path)))
            throw new InvalidDataException("CUE references must resolve to the exact staged BIN set.");
        string[] lines = (string[])_lines.Clone();
        string directory = Path.GetDirectoryName(toolCuePath)!;
        foreach (var (line, name) in _fileLines)
        {
            // chdman prefixes the CUE directory even for an absolute FILE reference.
            // Only these generated relative paths may traverse: resolution is pinned to the held staged BIN.
            string relative = Path.GetRelativePath(directory, paths[name]);
            if (Path.IsPathFullyQualified(relative) || Path.GetFullPath(Path.Combine(directory, relative)) != paths[name])
                throw new InvalidDataException("BIN cannot be referenced from the staged descriptor.");
            lines[line] = $"FILE \"{relative}\" BINARY";
        }
        byte[] output = Encoding.UTF8.GetBytes(string.Join('\n', lines));
        // Absolute staging paths can be longer than original names, but remain bounded.
        if (output.Length > ChdCdRequest.MaximumBytes)
            throw new InvalidDataException("Rendered CUE exceeds its budget.");
        return output;
    }

    public static bool PortableName(string? name) => !string.IsNullOrWhiteSpace(name) && name.Length <= 240 &&
        name is not ("." or "..") && !name.EndsWith('.') && !name.EndsWith(' ') &&
        !name.Any(c => char.IsControl(c) || "/\\:*?\"<>|".Contains(c));
}

/// <summary>Strict internal CD request/reply serialization. Required fields, duplicate detection and bounded reads.</summary>
public static class ChdCdJson
{
    public static JsonSerializerOptions Options { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true,
        RespectNullableAnnotations = true,
        MaxDepth = 8,
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) }
    };
    public static T Read<T>(ReadOnlyMemory<byte> bytes, int maximumBytes)
    {
        if (bytes.Length is < 1 || bytes.Length > maximumBytes)
            throw new InvalidDataException("CD JSON exceeds its budget.");
        using var doc = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 8 });
        Unique(doc.RootElement);
        return JsonSerializer.Deserialize<T>(bytes.Span, Options) ?? throw new InvalidDataException("Missing CD JSON.");
    }
    public static void Unique(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            HashSet<string> names = new(StringComparer.Ordinal);
            foreach (var p in value.EnumerateObject())
            { if (!names.Add(p.Name)) throw new InvalidDataException("Duplicate CD JSON field."); Unique(p.Value); }
        }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var item in value.EnumerateArray())
                Unique(item);
    }
    public static bool Hash(string? value, int length) => value?.Length == length && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f') &&
        (length == 8 || value.Any(c => c != '0'));
    public static bool Absolute(string? path) => path is { Length: > 0 and <= 2048 } && Path.IsPathFullyQualified(path) &&
        !path.Any(c => char.IsControl(c) || c == '"') && Path.GetFullPath(path) == path;
}
