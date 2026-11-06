using System.Buffers.Binary;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Moonlark.Native.Engineering.Chdman;
using Moonlark.Native.Engineering.Core;
using Moonlark.Native.Engineering.Fixtures;
using Moonlark.Native.Engineering.Tests.Native;
using Moonlark.Native.Engineering.Tests.TestSupport;

namespace Moonlark.Native.Engineering.Tests.Fixtures;

/// <summary>The CHD v5 header fields the direct parser reads, followed by one checksummed metadata entry.</summary>
internal sealed record SyntheticChd(ulong LogicalBytes, uint HunkBytes, uint UnitBytes, string RawSha1, string OverallSha1, string Tag, byte[] Metadata)
{
    internal string[]? TrackMetadata { get; init; }

    private const int HeaderBytes = 124;
    private const int MetadataHeaderBytes = 16;

    /// <summary>A header consistent with <paramref name="logical"/>: its length, its SHA-1, and the overall SHA-1 over the entry.</summary>
    internal static SyntheticChd For(byte[] logical, uint hunkBytes, uint unitBytes, string tag, byte[] metadata)
    {
        string raw = Sha1(logical);
        // With one checksummed entry the overall SHA-1 is SHA-1(raw SHA-1, tag, SHA-1(value)); no ordering is involved.
        string overall = Sha1([.. Convert.FromHexString(raw), .. Encoding.ASCII.GetBytes(tag), .. Convert.FromHexString(Sha1(metadata))]);
        return new SyntheticChd((ulong)logical.Length, hunkBytes, unitBytes, raw, overall, tag, metadata);
    }

    internal static string Sha1(ReadOnlySpan<byte> data)
    {
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        hash.AppendData(data);
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    internal byte[] ToBytes()
    {
        byte[][] entries = TrackMetadata is null ? [Metadata] : TrackMetadata.Select(text => Encoding.ASCII.GetBytes(text + '\0')).ToArray();
        byte[] data = new byte[HeaderBytes + entries.Sum(entry => MetadataHeaderBytes + entry.Length)];
        "MComprHD"u8.CopyTo(data);
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(8), HeaderBytes);
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(12), 5);
        BinaryPrimitives.WriteUInt64BigEndian(data.AsSpan(32), LogicalBytes);
        BinaryPrimitives.WriteUInt64BigEndian(data.AsSpan(48), HeaderBytes);
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(56), HunkBytes);
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(60), UnitBytes);
        Convert.FromHexString(RawSha1).CopyTo(data, 64);
        Convert.FromHexString(OverallSha1).CopyTo(data, 84);
        int offset = HeaderBytes;
        for (int index = 0; index < entries.Length; index++)
        {
            byte[] entry = entries[index];
            Encoding.ASCII.GetBytes(Tag).CopyTo(data, offset);
            BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(offset + 4), 0x0100_0000u | (uint)entry.Length);
            int next = offset + MetadataHeaderBytes + entry.Length;
            BinaryPrimitives.WriteUInt64BigEndian(data.AsSpan(offset + 8), index + 1 < entries.Length ? (ulong)next : 0);
            entry.CopyTo(data, offset + MetadataHeaderBytes);
            offset = next;
        }
        if (TrackMetadata is not null)
        {
            ChdHeader parsed = ChdHeader.Parse(data).Value;
            Convert.FromHexString(ChdHeader.ComputeOverallSha1(RawSha1, parsed.Metadata)).CopyTo(data, 84);
        }
        return data;
    }
}

/// <summary>
/// A fake chdman for one test: a POSIX script that copies a prepared file for each <c>-o</c>/<c>-ob</c> output and prints a
/// prepared response for <c>verify</c>; it encodes nothing. Every prepared file starts consistent, so a test changes one thing.
/// </summary>
internal sealed class FakeChdman : IDisposable
{
    internal const string Verified = "Raw SHA1 verification successful!\nOverall SHA1 verification successful!\n";

    internal static readonly (string Name, string Diagnostic)[] NegativeCases =
    [
        ("raw-mismatch", "Error: Raw SHA1 in header"),
        ("overall-mismatch", "Error: Overall SHA1 in header"),
        ("raw-missing", "No verification to be done; CHD has no checksum"),
    ];

    private readonly TemporaryDirectory _directory = new();
    private readonly Dictionary<string, SyntheticChd> _headers = new(StringComparer.Ordinal);

    [UnsupportedOSPlatform("windows")]
    internal FakeChdman()
    {
        Root = Path.Combine(_directory.Path, "root");
        TestRepository.CopyTo(Root, MamePin.PinPath);
        Prepared = Directory.CreateDirectory(Path.Combine(_directory.Path, "prepared")).FullName;
        string tools = Directory.CreateDirectory(Path.Combine(_directory.Path, "tool")).FullName;
        Tool = Path.Combine(tools, "chdman");
        FakeTools.WriteExecutable(Tool, Script(Prepared));
        Manifest = Path.Combine(tools, "build-manifest.json");
        File.WriteAllText(Manifest, ReceiptJson.Serialize(new JsonObject
        {
            ["schemaVersion"] = ChdmanBuild.ReceiptSchemaVersion,
            ["recipeVersion"] = ChdmanBuild.RecipeVersion,
            ["pin"] = MamePin.ReadDocument(Root),
            ["recipeSha256"] = new string('f', 64),
            ["binary"] = new JsonObject { ["path"] = "native/chdman", ["sha256"] = Digest.Sha256File(Tool), ["bytes"] = new FileInfo(Tool).Length },
        }));
        Output = Path.Combine(Root, "artifacts", "fixtures", "libchdr");
        Log = Path.Combine(_directory.Path, "fixtures.log");
        foreach (string codec in FixtureGenerator.DvdCodecs)
            Prepare("dvd-" + codec, Synthetic("dvd-" + codec), (".extracted.iso", SyntheticSources.Dvd()));
        foreach (string codec in FixtureGenerator.CdCodecs)
            Prepare("cd-" + codec, Synthetic("cd-" + codec), (".extracted.bin", SyntheticSources.Cd()), (".extracted.cue", "synthetic cue"u8.ToArray()));
        Prepare("cd-subcode", SyntheticSources.CdSubcode(), (".extracted.bin", SyntheticSources.CdSubcode()), (".extracted.toc", "synthetic toc"u8.ToArray()));
        foreach (string stem in CdEdgeFixtures.Stems)
        {
            Prepare(stem, CdEdgeFixtures.Logical(stem), (".extracted.bin", CdEdgeFixtures.Source(stem)), (".extracted.cue", "synthetic cue"u8.ToArray()));
            WriteHeader(stem, _headers[stem] with { TrackMetadata = CdEdgeFixtures.Metadata(stem) });
            ChdHeader header = ChdHeader.Read(Path.Combine(Prepared, stem + ".chd")).Value;
            _headers[stem] = _headers[stem] with { OverallSha1 = header.OverallSha1 };
            if (stem == "cd-edge-subcode")
            {
                byte[] toc = CdEdgeFixtures.Source(stem);
                CdEdgeFixtures.SwapSubcodeAudio(toc);
                Replace(stem + ".extracted.toc.bin", toc);
                Replace(stem + ".extracted.toc", "synthetic toc"u8.ToArray());
            }
        }
        foreach ((string Codec, uint HunkBytes) recipe in FixtureGenerator.DvdRecipes)
        {
            string stem = FixtureGenerator.DvdRecipeStem(recipe);
            Prepare(stem, Synthetic(stem), recipe.HunkBytes, (".extracted.iso", SyntheticSources.Dvd()));
        }
        Respond("dvd-none.chd", "No verification to be done; CHD is uncompressed\n");
        foreach ((string name, string diagnostic) in NegativeCases)
            Respond("negative-" + name + ".chd", diagnostic + "\n");
    }

    internal string Root { get; }

    internal string Prepared { get; }

    internal string Tool { get; }

    internal string Manifest { get; }

    internal string Output { get; }

    internal string Log { get; }

    internal SyntheticChd Header(string stem) => _headers[stem];

    internal Result<JsonObject> Run() =>
        FixtureGenerator.Run(Root, TestRepository.CleanEnvironment, new FixtureRequest(Tool, Manifest, Output, Log));

    /// <summary>Replaces the header the fake reports for <paramref name="stem"/>.chd, leaving every other output unchanged.</summary>
    internal void ChangeHeader(string stem, Func<SyntheticChd, SyntheticChd> change) => WriteHeader(stem, change(_headers[stem]));

    /// <summary>Replaces the logical stream and keeps the header consistent with it.</summary>
    internal void ChangeLogical(string stem, byte[] logical)
    {
        SyntheticChd header = _headers[stem];
        Prepare(stem, logical, header.HunkBytes, header.UnitBytes, header.Tag, header.Metadata);
        if (header.TrackMetadata is not null)
            WriteHeader(stem, _headers[stem] with { TrackMetadata = header.TrackMetadata });
    }

    internal void Replace(string name, byte[] contents) => File.WriteAllBytes(Path.Combine(Prepared, name), contents);

    internal void Respond(string chd, string response) => File.WriteAllText(Path.Combine(Prepared, chd + ".verify"), response);

    internal static byte[] Corrupt(byte[] data)
    {
        byte[] copy = [.. data];
        copy[copy.Length / 2] ^= 1;
        return copy;
    }

    public void Dispose() => _directory.Dispose();

    private void Prepare(string stem, byte[] logical, params (string Suffix, byte[] Contents)[] extracted) =>
        Prepare(stem, logical, stem.StartsWith("dvd-", StringComparison.Ordinal) ? FixtureGenerator.DvdHunkBytes : FixtureGenerator.CdHunkBytes,
            extracted);

    private void Prepare(string stem, byte[] logical, uint hunkBytes, params (string Suffix, byte[] Contents)[] extracted)
    {
        bool dvd = stem.StartsWith("dvd-", StringComparison.Ordinal);
        Prepare(stem, logical, hunkBytes, dvd ? 2048u : 2448u, dvd ? "DVD " : "CHT2",
            Encoding.ASCII.GetBytes(dvd ? "synthetic dvd metadata" : "TRACK:1 TYPE:MODE1_RAW SUBTYPE:NONE FRAMES:16"));
        foreach ((string suffix, byte[] contents) in extracted)
            Replace(stem + suffix, contents);
        Respond(stem + ".chd", Verified);
    }

    private void Prepare(string stem, byte[] logical, uint hunkBytes, uint unitBytes, string tag, byte[] metadata)
    {
        Replace(stem + ".logical", logical);
        WriteHeader(stem, SyntheticChd.For(logical, hunkBytes, unitBytes, tag, metadata));
    }

    private void WriteHeader(string stem, SyntheticChd header)
    {
        _headers[stem] = header;
        File.WriteAllBytes(Path.Combine(Prepared, stem + ".chd"), header.ToBytes());
    }

    private static byte[] Synthetic(string stem) => Encoding.ASCII.GetBytes("synthetic logical stream for " + stem + "\n");

    [UnsupportedOSPlatform("windows")]
    private static string Script(string prepared) =>
        "#!/bin/sh\n"
        + "# Fake chdman: copies prepared outputs and prints prepared verify responses; it encodes nothing.\n"
        + "set -eu\n"
        + "prepared=" + FakeTools.Quote(prepared) + "\n"
        + "command=\"$1\"\nshift\ninput=\n"
        + "while [ \"$#\" -gt 0 ]; do\n"
        + "  case \"$1\" in\n"
        + "    -i) input=\"$2\"; shift 2 ;;\n"
        + "    -o|-ob) /bin/cp \"$prepared/$2\" \"$2\"; shift 2 ;;\n"
        + "    *) shift ;;\n"
        + "  esac\n"
        + "done\n"
        + "[ -f \"$input\" ] || { echo \"fake chdman: missing input $input\" >&2; exit 98; }\n"
        + "if [ \"$command\" = verify ]; then /bin/cat \"$prepared/$input.verify\"; fi\n";
}
