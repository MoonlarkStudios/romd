using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Moonlark.Native.Engineering.Core;

namespace Moonlark.Native.Engineering.Upstream;

/// <summary>
/// Rewrites only the upstream identity values in the pin and family props, keeping every other byte, then
/// re-parses the result to prove nothing else changed.
/// </summary>
internal static partial class AuthorityText
{
    internal static Result<string> RewritePin(string text, string commit, string upstreamVersion, IReadOnlyDictionary<string, string> headers)
    {
        if (RequirePlain([commit, upstreamVersion, .. headers.Keys, .. headers.Values]) is { } plain)
            return plain;
        Result<string> rewritten = ReplaceJsonString(text, "commit", commit).Then(next => ReplaceJsonString(next, "upstreamVersion", upstreamVersion));
        foreach ((string name, string digest) in headers)
            rewritten = rewritten.Then(next => ReplaceJsonString(next, name, digest));
        if (!rewritten.Succeeded)
            return rewritten;
        JsonObject expected = JsonNode.Parse(text)?.AsObject() ?? throw new InvalidDataException("libchdr pin must be a JSON object");
        expected["commit"] = commit;
        expected["upstreamVersion"] = upstreamVersion;
        if (expected["headers"] is not JsonObject expectedHeaders)
            return new Failure("Pin headers must be a JSON object");
        foreach ((string name, string digest) in headers)
            expectedHeaders[name] = digest;
        if (Check.That(JsonNode.DeepEquals(expected, JsonNode.Parse(rewritten.Value)),
            "Pin rewrite would change more than the upstream identity") is { } changed)
            return changed;
        return rewritten;
    }

    internal static Result<string> RewriteProps(string text, string commit, string upstreamVersion)
    {
        if (RequirePlain([commit, upstreamVersion]) is { } plain)
            return plain;
        Result<string> rewritten = ReplaceElement(text, "LibchdrUpstreamCommit", commit)
            .Then(next => ReplaceElement(next, "LibchdrUpstreamVersion", upstreamVersion));
        if (!rewritten.Succeeded)
            return rewritten;
        XDocument expected = XDocument.Parse(text);
        if (Check.That(SetProperty(expected, "LibchdrUpstreamCommit", commit) && SetProperty(expected, "LibchdrUpstreamVersion", upstreamVersion),
            "Props must define each upstream identity property exactly once") is { } single)
            return single;
        if (Check.That(XNode.DeepEquals(expected, XDocument.Parse(rewritten.Value)),
            "Props rewrite would change more than the upstream identity") is { } changed)
            return changed;
        return rewritten;
    }

    /// <summary>Values enter JSON, XML and C# source, so only tokens that need no escaping in any of them are accepted.</summary>
    private static Failure? RequirePlain(IEnumerable<string> values) =>
        values.FirstOrDefault(value => !PlainToken().IsMatch(value)) is { } invalid ? new Failure("Unexpected upstream identity value: " + invalid) : null;

    private static Result<string> ReplaceJsonString(string text, string key, string value)
    {
        var pattern = new Regex("(\"" + Regex.Escape(key) + "\"\\s*:\\s*\")[^\"\\\\]*(\")", RegexOptions.CultureInvariant);
        if (pattern.Count(text) != 1)
            return new Failure($"Pin must contain exactly one \"{key}\" string to update");
        return pattern.Replace(text, match => match.Groups[1].Value + value + match.Groups[2].Value, 1);
    }

    private static Result<string> ReplaceElement(string text, string name, string value)
    {
        var pattern = new Regex("(<" + name + ">)[^<]*(</" + name + ">)", RegexOptions.CultureInvariant);
        if (pattern.Count(text) != 1)
            return new Failure($"Props must contain exactly one {name} element to update");
        return pattern.Replace(text, match => match.Groups[1].Value + value + match.Groups[2].Value, 1);
    }

    private static bool SetProperty(XDocument props, string name, string value)
    {
        XElement[] matches = props.Root?.Elements("PropertyGroup").Elements(name).ToArray() ?? [];
        if (matches.Length != 1)
            return false;
        matches[0].Value = value;
        return true;
    }

    [GeneratedRegex(@"\A[0-9A-Za-z][0-9A-Za-z._+/-]*\z")]
    private static partial Regex PlainToken();
}
