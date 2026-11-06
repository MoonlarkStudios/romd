using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Moonlark.Native.Engineering.Chdman;
using Moonlark.Native.Engineering.Core;
using Moonlark.Native.Engineering.Fixtures;
using Moonlark.Native.Engineering.Tests.TestSupport;
using Xunit;

namespace Moonlark.Native.Engineering.Tests.Fixtures;

/// <summary>Fixture source and verification checks; no tools or game data required.</summary>
public sealed class FixtureGeneratorTests
{
    /// <summary>The v1 source identities never change.</summary>
    [Fact]
    public void V1SourceIdentitiesAreStable()
    {
        Assert.Equal("28fdd300e672af165872ba82ed0bb5e054949dd75cdccc57f017ce13ddf7b73b", Sha256(SyntheticSources.Dvd()));
        Assert.Equal("94fcd21e5ed2483fdad9bd02227ee158d807e9b8b1ca3b5c9016512ea051288a", Sha256(SyntheticSources.Cd()));
        Assert.Equal("d7560171224893384d0aff08e74fdcacfb25a18d14821fe2ec69b5edb9146b03", Sha256(Encoding.ASCII.GetBytes(SyntheticSources.CdCue)));
        Assert.Equal("66ef5fd87220361ff4aff84dc5c3e4b48dd9ec9955c03662bbf850a6b2fdc192", Sha256(SyntheticSources.CdSubcode()));
    }

    /// <summary>V2 source identities are new and pinned independently of historical reviewer bytes.</summary>
    [Theory]
    [InlineData("cd-edge-track1.bin", 39984, "ee283c3a901c7e4a572972b59054ce9d3f48ca52e91fcfec66ae1cc0af7b8c46")]
    [InlineData("cd-edge-track2.bin", 30576, "229e3871ab8966be9d9141de5537e8567842fccb1c1e334cc5d72a1a2b3c213a")]
    [InlineData("cd-edge-track3.bin", 21168, "f46596a3aff5199736ed469cefe4d53263871d6b3db5cc26dce4a5532143cdbe")]
    [InlineData("cd-edge-single.bin", 91728, "1af2bb031922ce4ef01f6bce50bea57591ff54ee188844d0007ca50d97d50c34")]
    [InlineData("cd-edge-sub1.bin", 19584, "a342821c1f6ead5d9ef164b18c07be8b5ee899d2e1a85375b0c3192d298495c4")]
    [InlineData("cd-edge-sub2.bin", 14688, "d2c1a85231eb955e2b9d27f250b626669cda80548956e1dea229651e3a5b0149")]
    [InlineData("cd-edge-multi.cue", 263, "eacba4a8fad664c041c65505da9702338d606083932d214154ba5360cfa95186")]
    [InlineData("cd-edge-partial.cue", 171, "8b723c44ec32f39a798a645b5c07d79b71aa611ebd9b3319008c2af7224c41e8")]
    [InlineData("cd-edge-single.cue", 197, "ce26d6fa29e669727937af1274a02f21c1dee7b7f213317b18a3a3dfd5f6ab82")]
    [InlineData("cd-edge-subcode.cue", 181, "953bfee1809711c28b122fcf59ac82d59f5d1e96fad86c9352db30aa680a0c7b")]
    public void V2SourceIdentitiesAreStable(string name, int bytes, string sha256)
    {
        byte[] source = CdEdgeFixtures.Sources()[name];
        Assert.Equal(bytes, source.Length);
        Assert.Equal(sha256, Sha256(source));
    }

    /// <summary>The DVD source is whole sectors with zero and duplicate hunks.</summary>
    [Fact]
    public void DvdSourceIsExactSectorLengthWithDuplicateAndZeroHunks()
    {
        byte[] source = SyntheticSources.Dvd();
        Assert.Equal(512 * 2048, source.Length);
        Assert.Equal(new byte[16384], source[..16384]);
        Assert.Equal(source[16384..32768], source[32768..49152]);
        Assert.NotEqual(new byte[16384], source[16384..32768]);
    }

    /// <summary>The CD source has raw MODE1 frames followed by distinct little-endian audio.</summary>
    [Fact]
    public void CdSourceHasRawModeOneAndDistinctLittleEndianAudio()
    {
        byte[] source = SyntheticSources.Cd();
        Assert.Equal(32 * 2352, source.Length);
        byte[] syncAndAddress = [0, .. Enumerable.Repeat((byte)0xFF, 10), 0, 0, 2, 0, 1];
        Assert.Equal(syncAndAddress, source[..16]);
        Assert.Equal<(short, short)>((-32768, -32768), Samples(source, 16 * 2352));
        Assert.Equal<(short, short)>((-32737, -32725), Samples(source, 16 * 2352 + 4));
        Assert.Contains("INDEX 00 00:00:16", SyntheticSources.CdCue, StringComparison.Ordinal);
        Assert.Contains("INDEX 01 00:00:20", SyntheticSources.CdCue, StringComparison.Ordinal);
    }

    /// <summary>Each subcode frame keeps the raw sector and appends deterministic nonzero subcode.</summary>
    [Fact]
    public void SubcodeSourcePreservesRawSectorAndNonzeroSubcodePerFrame()
    {
        byte[] source = SyntheticSources.CdSubcode();
        byte[] data = SyntheticSources.Cd();
        Assert.Equal(16 * 2448, source.Length);
        for (int frame = 0; frame < 16; frame++)
        {
            Assert.Equal(data[(frame * 2352)..((frame + 1) * 2352)], source[(frame * 2448)..(frame * 2448 + 2352)]);
            Assert.Equal(Enumerable.Range(0, 96).Select(index => (byte)((frame * 11 + index * 7) & 255)), source[(frame * 2448 + 2352)..((frame + 1) * 2448)]);
        }
        Assert.Contains("RW_RAW", SyntheticSources.CdSubcodeToc, StringComparison.Ordinal);
    }

    /// <summary>Neither a zero exit nor raw success alone establishes overall integrity.</summary>
    [Theory]
    [InlineData("")]
    [InlineData("Raw SHA1 verification successful!\n")]
    [InlineData("Raw SHA1 verification successful!\nError: Overall SHA1 in header")]
    [InlineData("No verification to be done")]
    [InlineData("Raw SHA1 verification successful!\nOverall SHA1 verification successful!\n\nError: later operation failed")]
    public void VerifyExitSuccessOrRawSuccessDoesNotEstablishOverallIntegrity(string response)
    {
        Assert.True(FixtureGenerator.VerifySucceeded("Raw SHA1 verification successful!\nOverall SHA1 verification successful!\n"));
        Assert.False(FixtureGenerator.VerifySucceeded(response));
    }

    /// <summary>The overall hash sorts checksummed metadata bytewise and excludes unchecked metadata.</summary>
    [Fact]
    public void OverallHashSortsChecksummingMetadataAndExcludesUncheckedMetadata()
    {
        string raw = string.Concat(Enumerable.Repeat("ab", 20));
        ChdMetadata[] metadata =
        [
            new("ZZZZ", 1, 0, string.Concat(Enumerable.Repeat("22", 20)), ""),
            new("AAAA", 1, 0, string.Concat(Enumerable.Repeat("11", 20)), ""),
            new("CCCC", 0, 0, string.Concat(Enumerable.Repeat("33", 20)), ""),
        ];
        byte[] expected = [.. Convert.FromHexString(raw), .. "AAAA"u8, .. Enumerable.Repeat((byte)0x11, 20), .. "ZZZZ"u8, .. Enumerable.Repeat((byte)0x22, 20)];
        Assert.Equal(ChdHeader.Sha1(expected), ChdHeader.ComputeOverallSha1(raw, metadata));
    }

    /// <summary>The direct parser rejects a metadata cycle before visiting an entry twice.</summary>
    [Fact]
    public void DirectParserRejectsMetadataCyclesBeforeRepeatedVisit()
    {
        byte[] data = Header(141, metadataOffset: 124);
        Entry(data, 124, "TEST", 0x01000001, next: 124);
        data[140] = 1;
        Assert.Contains("Cyclic", ChdHeader.Parse(data).Failure.Message, StringComparison.Ordinal);
    }

    /// <summary>Chains longer than the visit limit are rejected even without a cycle.</summary>
    [Fact]
    public void DirectParserRejectsExcessiveMetadata()
    {
        byte[] data = Header(124 + 129 * 16, metadataOffset: 124);
        for (int index = 0; index < 129; index++)
            Entry(data, 124 + index * 16, "TEST", 0, next: index == 128 ? 0 : (ulong)(124 + (index + 1) * 16));
        Assert.Contains("excessive", ChdHeader.Parse(data).Failure.Message, StringComparison.Ordinal);
    }

    /// <summary>Huge offsets and lengths are bounds failures, never unsigned wraparound.</summary>
    [Theory]
    [InlineData(0xFFFF_FFFF_FFFF_FFF8UL, 0, "Metadata header outside fixture")]
    [InlineData(130UL, 0, "Metadata header outside fixture")]
    [InlineData(124UL, 0x00FF_FFFF, "Metadata value outside fixture")]
    [InlineData(124UL, 2, "Metadata value outside fixture")]
    public void DirectParserBoundsMetadataWithoutOverflow(ulong offset, uint flagsLength, string message)
    {
        byte[] data = Header(141, metadataOffset: offset);
        Entry(data, 124, "TEST", flagsLength, next: 0);
        Assert.Contains(message, ChdHeader.Parse(data).Failure.Message, StringComparison.Ordinal);
    }

    /// <summary>Non-v5 data and non-ASCII metadata tags fail instead of being decoded loosely.</summary>
    [Fact]
    public void DirectParserRejectsOtherVersionsAndNonAsciiTags()
    {
        byte[] version = Header(124, metadataOffset: 0);
        BinaryPrimitives.WriteUInt32BigEndian(version.AsSpan(12), 4);
        Assert.Contains("Expected CHD v5 fixture", ChdHeader.Parse(version).Failure.Message, StringComparison.Ordinal);
        Assert.Contains("Expected CHD v5 fixture", ChdHeader.Parse(new byte[123]).Failure.Message, StringComparison.Ordinal);
        byte[] tag = Header(140, metadataOffset: 124);
        Entry(tag, 124, "TEST", 0, next: 0);
        tag[124] = 0xC3;
        Assert.Contains("not ASCII", ChdHeader.Parse(tag).Failure.Message, StringComparison.Ordinal);
    }

    /// <summary>Header fields and every metadata entry are reported exactly.</summary>
    [Fact]
    public void DirectParserReadsHeaderFieldsAndMetadataChain()
    {
        byte[] data = Header(124 + 19 + 16, metadataOffset: 124);
        BinaryPrimitives.WriteUInt64BigEndian(data.AsSpan(32), 1048576);
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(56), 16384);
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(60), 2048);
        data.AsSpan(64, 20).Fill(0xAA);
        data.AsSpan(84, 20).Fill(0xBB);
        Entry(data, 124, "DVD ", 0x01000003, next: 143);
        "abc"u8.CopyTo(data.AsSpan(140));
        Entry(data, 143, "UNCK", 0, next: 0);
        ChdHeader header = ChdHeader.Parse(data).Value;
        Assert.Equal((1048576UL, 16384U, 2048U), (header.LogicalBytes, header.HunkBytes, header.UnitBytes));
        Assert.Equal((new string('a', 40), new string('b', 40), new string('0', 40)), (header.RawSha1, header.OverallSha1, header.ParentSha1));
        ChdMetadata[] metadata = [new("DVD ", 1, 3, ChdHeader.Sha1("abc"u8), "616263"), new("UNCK", 0, 0, ChdHeader.Sha1([]), "")];
        Assert.Equal<ChdMetadata>(metadata, header.Metadata);
    }

    /// <summary>The tool must be the pinned receipt's exact regular-file binary from the current chdman build-recipe version.</summary>
    [Fact]
    public void ToolMustMatchThePinAndReceiptBinary()
    {
        using var directory = new TemporaryDirectory();
        string tool = Path.Combine(directory.Path, "chdman");
        File.WriteAllBytes(tool, "synthetic tool"u8.ToArray());
        string manifest = Path.Combine(directory.Path, "build-manifest.json");
        JsonObject receipt = Receipt(tool);
        File.WriteAllText(manifest, ReceiptJson.Serialize(receipt));
        Assert.True(JsonNode.DeepEquals(receipt, FixtureGenerator.VerifyTool(TestRepository.Root, tool, manifest).Value));
        Assert.Contains("Tool pin/manifest mismatch", Verify(directory.Path, tool, json => json["pin"]!["commit"] = new string('0', 40)), StringComparison.Ordinal);
        Assert.Contains("Tool pin/manifest mismatch", Verify(directory.Path, tool, json => json["schemaVersion"] = 1), StringComparison.Ordinal);
        Assert.Contains("Tool pin/manifest mismatch", Verify(directory.Path, tool, json => json["recipeVersion"] = ChdmanBuild.RecipeVersion + 1), StringComparison.Ordinal);
        Assert.Contains("Tool pin/manifest mismatch", Verify(directory.Path, tool, json => json.Remove("recipeVersion")), StringComparison.Ordinal);
        Assert.Contains("Tool binary digest/size mismatch", Verify(directory.Path, tool, json => json["binary"]!["sha256"] = new string('0', 64)), StringComparison.Ordinal);
        Assert.Contains("Tool binary digest/size mismatch", Verify(directory.Path, tool, json => json["binary"]!["bytes"] = 1), StringComparison.Ordinal);
        string link = Path.Combine(directory.Path, "link");
        File.CreateSymbolicLink(link, tool);
        Assert.Contains("Tool must be a regular file", FixtureGenerator.VerifyTool(TestRepository.Root, link, manifest).Failure.Message, StringComparison.Ordinal);
    }

    /// <summary>Output must be a strict subdirectory of the ignored fixtures tree.</summary>
    [Fact]
    public void OutputMustBeAFixturesSubdirectory()
    {
        using var directory = new TemporaryDirectory();
        string root = directory.Path;
        Assert.Contains("fixtures subdirectory", FixtureGenerator.ValidateOutput(root, Path.Combine(root, "artifacts", "fixtures")).Failure.Message, StringComparison.Ordinal);
        Assert.Contains("fixtures subdirectory", FixtureGenerator.ValidateOutput(root, Path.Combine(root, "artifacts", "tools", "fixtures")).Failure.Message, StringComparison.Ordinal);
        Assert.Contains("ignored artifacts directory", FixtureGenerator.ValidateOutput(root, Path.Combine(root, "fixtures", "libchdr")).Failure.Message, StringComparison.Ordinal);
        Assert.Equal(Path.Combine(ArtifactsPath.Resolve(root), "artifacts", "fixtures", "libchdr"),
            FixtureGenerator.ValidateOutput(root, Path.Combine(root, "artifacts", "fixtures", "libchdr")).Value);
    }

    private static string Verify(string directory, string tool, Action<JsonObject> change)
    {
        JsonObject receipt = Receipt(tool);
        change(receipt);
        string manifest = Path.Combine(directory, "changed-manifest.json");
        File.WriteAllText(manifest, ReceiptJson.Serialize(receipt));
        return FixtureGenerator.VerifyTool(TestRepository.Root, tool, manifest).Failure.Message;
    }

    private static JsonObject Receipt(string tool) => new()
    {
        ["schemaVersion"] = ChdmanBuild.ReceiptSchemaVersion,
        ["recipeVersion"] = ChdmanBuild.RecipeVersion,
        ["pin"] = MamePin.ReadDocument(TestRepository.Root),
        ["recipeSha256"] = new string('f', 64),
        ["binary"] = new JsonObject { ["path"] = "native/chdman", ["sha256"] = Digest.Sha256File(tool), ["bytes"] = new FileInfo(tool).Length },
    };

    private static byte[] Header(int length, ulong metadataOffset)
    {
        byte[] data = new byte[length];
        "MComprHD"u8.CopyTo(data);
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(8), 124);
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(12), 5);
        BinaryPrimitives.WriteUInt64BigEndian(data.AsSpan(48), metadataOffset);
        return data;
    }

    private static void Entry(byte[] data, int offset, string tag, uint flagsLength, ulong next)
    {
        Encoding.ASCII.GetBytes(tag).CopyTo(data, offset);
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(offset + 4), flagsLength);
        BinaryPrimitives.WriteUInt64BigEndian(data.AsSpan(offset + 8), next);
    }

    private static (short Left, short Right) Samples(byte[] data, int offset) =>
        (BinaryPrimitives.ReadInt16LittleEndian(data.AsSpan(offset)), BinaryPrimitives.ReadInt16LittleEndian(data.AsSpan(offset + 2)));

    private static string Sha256(byte[] data) => Convert.ToHexStringLower(SHA256.HashData(data));
}
