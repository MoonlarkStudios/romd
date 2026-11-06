using System.Collections.Immutable;
using System.Text.Json.Nodes;
using Moonlark.Native.Engineering.Core;

namespace Moonlark.Native.Engineering.Native;

internal sealed record CacheEntry(string Name, string Type, string Value);

internal sealed record CompilerIdentity(string Id, string Version, string Path);

/// <summary>The effective configuration CMake actually used, as reported by its File API.</summary>
internal sealed record CMakeReply(ImmutableArray<CacheEntry> Cache, CompilerIdentity Compiler);

/// <summary>Stateless File API queries written before configure; the reply is read back instead of trusting arguments.</summary>
internal static class CMakeFileApi
{
    private static readonly string[] Queries = ["cache-v2", "toolchains-v1"];

    internal static void WriteQueries(string buildDirectory)
    {
        string query = Directory.CreateDirectory(Path.Combine(ApiDirectory(buildDirectory), "query")).FullName;
        foreach (string name in Queries)
            File.WriteAllText(Path.Combine(query, name), "");
    }

    internal static Result<CMakeReply> Read(string buildDirectory)
    {
        string reply = Path.Combine(ApiDirectory(buildDirectory), "reply");
        string[] indexes = Directory.Exists(reply) ? Directory.GetFiles(reply, "index-*.json") : [];
        if (Check.That(indexes.Length == 1, "CMake File API must produce exactly one reply index") is { } count)
            return count;
        Result<JsonObject> index = JsonFields.ReadObject(indexes[0], "CMake File API index");
        if (!index.Succeeded)
            return index.Failure;
        if (index.Value["reply"] is not JsonObject replies)
            return new Failure("CMake File API index has no reply object");
        Result<JsonObject> cache = ReplyObject(reply, replies, "cache-v2", "cache", 2);
        if (!cache.Succeeded)
            return cache.Failure;
        Result<JsonObject> toolchains = ReplyObject(reply, replies, "toolchains-v1", "toolchains", 1);
        if (!toolchains.Succeeded)
            return toolchains.Failure;
        Result<ImmutableArray<CacheEntry>> entries = Entries(cache.Value);
        if (!entries.Succeeded)
            return entries.Failure;
        Result<CompilerIdentity> compiler = Compiler(toolchains.Value);
        return compiler.Succeeded ? new CMakeReply(entries.Value, compiler.Value) : compiler.Failure;
    }

    private static string ApiDirectory(string buildDirectory) => Path.Combine(buildDirectory, ".cmake", "api", "v1");

    private static Result<JsonObject> ReplyObject(string directory, JsonObject replies, string query, string kind, int major)
    {
        if (replies[query] is not JsonObject reference)
            return new Failure("CMake File API did not answer " + query);
        Result<string> file = JsonFields.RequireString(reference, "jsonFile");
        if (!file.Succeeded)
            return new Failure($"CMake File API rejected {query}: {reference["error"]?.ToJsonString() ?? "no reply"}");
        if (Check.That(file.Value.Length > 0 && file.Value == Path.GetFileName(file.Value) && file.Value is not ("." or "..")
            && !file.Value.Contains('/', StringComparison.Ordinal) && !file.Value.Contains('\\', StringComparison.Ordinal),
            "Unsafe CMake File API reply path: " + file.Value) is { } unsafePath)
            return unsafePath;
        Result<JsonObject> value = JsonFields.ReadObject(Path.Combine(directory, file.Value), "CMake File API " + query);
        if (!value.Succeeded)
            return value.Failure;
        Result<string> actualKind = JsonFields.RequireString(value.Value, "kind");
        Result<long> actualMajor = value.Value["version"] is JsonObject version ? JsonFields.RequireInteger(version, "major") : new Failure("");
        bool expected = actualKind.Succeeded && actualKind.Value == kind && actualMajor.Succeeded && actualMajor.Value == major;
        return expected ? value.Value : new Failure($"Unexpected CMake File API {query} reply");
    }

    private static Result<ImmutableArray<CacheEntry>> Entries(JsonObject cache)
    {
        if (cache["entries"] is not JsonArray entries)
            return new Failure("CMake cache reply has no entries");
        ImmutableArray<CacheEntry>.Builder result = ImmutableArray.CreateBuilder<CacheEntry>(entries.Count);
        foreach (JsonNode? node in entries)
        {
            if (node is not JsonObject entry)
                return new Failure("Unrecognized CMake cache entry");
            Result<string> name = JsonFields.RequireString(entry, "name");
            Result<string> type = JsonFields.RequireString(entry, "type");
            Result<string> value = JsonFields.RequireString(entry, "value");
            if (!name.Succeeded || !type.Succeeded || !value.Succeeded)
                return new Failure("Unrecognized CMake cache entry");
            result.Add(new CacheEntry(name.Value, type.Value, value.Value));
        }
        return result.MoveToImmutable();
    }

    private static Result<CompilerIdentity> Compiler(JsonObject toolchains)
    {
        JsonObject[] languages = toolchains["toolchains"] is JsonArray all
            ? [.. all.OfType<JsonObject>().Where(item => JsonFields.RequireString(item, "language") is { Succeeded: true } language && language.Value == "C")]
            : [];
        if (languages is not [{ } only] || only["compiler"] is not JsonObject compiler)
            return new Failure("CMake did not identify exactly one C toolchain");
        Result<string> id = JsonFields.RequireString(compiler, "id");
        Result<string> version = JsonFields.RequireString(compiler, "version");
        Result<string> path = JsonFields.RequireString(compiler, "path");
        return id.Succeeded && version.Succeeded && path.Succeeded && id.Value.Length > 0 && version.Value.Length > 0
            ? new CompilerIdentity(id.Value, version.Value, path.Value)
            : new Failure("CMake did not identify the C compiler and its version");
    }
}
