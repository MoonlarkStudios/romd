using System.Text.Json.Nodes;
using Moonlark.Native.Engineering.Core;

namespace Moonlark.Native.Engineering.Chdman;

/// <summary>The reviewed MAME source archive pin. <see cref="Document"/> is recorded verbatim in tool receipts.</summary>
internal sealed record MamePin(JsonObject Document, string Tag, string Commit, string Version, long SourceBytes, string SourceSha256)
{
    internal const string PinPath = "native/chdman/mame.json";
    internal const string Repository = "https://github.com/mamedev/mame";

    internal string ArchivePrefix => "mame-" + Tag;

    internal static JsonObject ReadDocument(string root) =>
        JsonNode.Parse(File.ReadAllText(Path.Combine(root, PinPath))) as JsonObject
            ?? throw new InvalidDataException("MAME pin must be a JSON object");

    internal static Result<MamePin> Read(string root)
    {
        JsonObject pin = ReadDocument(root);
        string tag = Text(pin, "tag");
        string commit = Text(pin, "commit");
        string version = Text(pin, "version");
        string digest = Text(pin, "sourceSha256");
        long bytes = Integer(pin, "sourceBytes");
        if (Check.That(Text(pin, "repository") == Repository, "Unexpected MAME repository") is { } repository)
            return repository;
        if (Check.That(Authorities.Sha256().IsMatch(digest), "Invalid archive digest") is { } invalidDigest)
            return invalidDigest;
        if (Check.That(Authorities.Sha1().IsMatch(commit), "Invalid source commit") is { } invalidCommit)
            return invalidCommit;
        if (Check.That(bytes > 0 && Integer(pin, "rebuildRevision") > 0, "Invalid source size/revision") is { } size)
            return size;
        if (Check.That(version.Length > 0 && tag == "mame" + version.Replace(".", "", StringComparison.Ordinal),
            "Tag/version mismatch") is { } mismatch)
            return mismatch;
        return new MamePin(pin, tag, commit, version, bytes, digest);
    }

    private static string Text(JsonObject pin, string name) =>
        pin[name] is JsonValue value && value.TryGetValue(out string? text) ? text : "";

    private static long Integer(JsonObject pin, string name) =>
        pin[name] is JsonValue value && value.TryGetValue(out long number) ? number : 0;
}
