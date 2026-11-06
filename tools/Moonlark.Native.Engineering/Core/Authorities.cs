using System.Collections.Immutable;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Moonlark.Native.Engineering.Core;

/// <summary>The reviewed libchdr source pin. <see cref="Document"/> is recorded verbatim in recipes.</summary>
internal sealed record LibchdrPin(JsonObject Document, string Repository, string Commit, string UpstreamVersion,
    IReadOnlyDictionary<string, string> Headers, IReadOnlyList<string> Features, IReadOnlyList<string> SupportedRids);

internal sealed record LibchdrAuthority(LibchdrPin Pin, string ManagedVersion, XDocument Props);

internal static partial class Authorities
{
    internal const string PinPath = "native/libchdr/libchdr.json";
    internal const string PropsPath = "native/libchdr/libchdr.props";
    internal const string Repository = "https://github.com/rtissera/libchdr";
    internal static ImmutableArray<string> HeaderPaths { get; } = ["include/libchdr/chd.h", "include/libchdr/coretypes.h"];
    internal static ImmutableArray<string> Features { get; } = ["raw-sectors", "subcode", "block-crc"];

    internal static Result<LibchdrAuthority> ReadLibchdr(string root)
    {
        JsonObject pin = JsonNode.Parse(File.ReadAllText(Path.Combine(root, PinPath)))?.AsObject()
            ?? throw new InvalidDataException("libchdr pin must be a JSON object");
        XDocument props = XDocument.Load(Path.Combine(root, PropsPath));
        Result<string> version = PropertyValue(props, "LibchdrManagedVersion");
        if (!version.Succeeded)
            return version.Failure;
        if (ValidateSemVer(version.Value) is { } semver)
            return semver;
        Result<string> mode = PropertyValue(props, "LibchdrNativeVersionMode");
        if (!mode.Succeeded)
            return mode.Failure;
        if (Check.That(mode.Value == "Family", "Native mode must be Family") is { } family)
            return family;
        string commit = (string?)pin["commit"] ?? "";
        string upstreamVersion = (string?)pin["upstreamVersion"] ?? "";
        if (Check.That(Sha1().IsMatch(commit), "Invalid pinned commit") is { } invalid)
            return invalid;
        Result<string> propsCommit = PropertyValue(props, "LibchdrUpstreamCommit");
        if (!propsCommit.Succeeded)
            return propsCommit.Failure;
        if (Check.That(commit == propsCommit.Value, "Props/pin commit mismatch") is { } commitMismatch)
            return commitMismatch;
        Result<string> propsUpstream = PropertyValue(props, "LibchdrUpstreamVersion");
        if (!propsUpstream.Succeeded)
            return propsUpstream.Failure;
        if (Check.That(upstreamVersion == propsUpstream.Value, "Props/pin upstream version mismatch") is { } versionMismatch)
            return versionMismatch;
        if (Check.That((string?)pin["repository"] == Repository, "Unexpected upstream repository") is { } repository)
            return repository;
        string[] rids = Strings(pin["supportedRids"]);
        if (Check.That(SameSet(rids, NativeRids.Supported), "Supported RID contract changed") is { } ridContract)
            return ridContract;
        string[] features = Strings(pin["features"]);
        if (Check.That(SameSet(features, Features), "Required features changed") is { } featureContract)
            return featureContract;
        JsonObject headerObject = pin["headers"] as JsonObject ?? [];
        Dictionary<string, string> headers = headerObject.ToDictionary(pair => pair.Key, pair => (string?)pair.Value ?? "", StringComparer.Ordinal);
        if (Check.That(SameSet(headers.Keys, HeaderPaths), "Header authority must contain exactly the two ABI headers") is { } headerSet)
            return headerSet;
        foreach ((string name, string digest) in headers)
            if (Check.That(Sha256().IsMatch(digest), "Invalid header digest: " + name) is { } badDigest)
                return badDigest;
        return new LibchdrAuthority(new LibchdrPin(pin, Repository, commit, upstreamVersion, headers, features, rids), version.Value, props);
    }

    /// <summary>Exactly one nonempty top-level property definition is the authority for a name.</summary>
    internal static Result<string> PropertyValue(XDocument props, string name)
    {
        XElement[] matches = props.Root?.Elements("PropertyGroup").Elements(name).ToArray() ?? [];
        return matches.Length == 1 && matches[0].Value.Length > 0 ? matches[0].Value : new Failure("One authority required for " + name);
    }

    internal static Failure? ValidateSemVer(string version)
    {
        Match match = SemVer().Match(version);
        if (!match.Success)
            return new Failure($"Invalid managed SemVer: '{version}'");
        if (!match.Groups[4].Success)
            return null;
        return Check.That(match.Groups[4].Value.Split('.').All(item => !item.All(char.IsAsciiDigit) || item == "0" || !item.StartsWith('0')),
            "Numeric prerelease identifiers must not have leading zeroes");
    }

    internal static string[] Strings(JsonNode? node) => node is JsonArray array ? array.Select(item => (string?)item ?? "").ToArray() : [];

    internal static bool SameSet(IEnumerable<string> actual, IEnumerable<string> expected) =>
        actual.ToHashSet(StringComparer.Ordinal).SetEquals(expected);

    [GeneratedRegex("^[0-9a-f]{40}$")]
    internal static partial Regex Sha1();

    [GeneratedRegex("^[0-9a-f]{64}$")]
    internal static partial Regex Sha256();

    [GeneratedRegex(@"\A(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(?:-([0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?(?:\+([0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?\z")]
    private static partial Regex SemVer();
}
