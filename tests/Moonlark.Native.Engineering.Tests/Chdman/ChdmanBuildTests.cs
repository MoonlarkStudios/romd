using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Moonlark.Native.Engineering.Chdman;
using Moonlark.Native.Engineering.Cli;
using Moonlark.Native.Engineering.Core;
using Moonlark.Native.Engineering.Tests.TestSupport;
using Xunit;

namespace Moonlark.Native.Engineering.Tests.Chdman;

/// <summary>Archive and execution-contract checks for the pinned chdman recipe, without installing or executing tools.</summary>
public sealed class ChdmanBuildTests
{
    private const string Prefix = "mame-mame000";

    /// <summary>Unsafe paths and links outside the tree, as in the Python recipe tests.</summary>
    public static TheoryData<string, string?> UnsafeMembers { get; } = new()
    {
        { Prefix + "/../escape", null },
        { "/" + Prefix + "/file", null },
        { "other/file", null },
        { Prefix + "/link", "../outside" },
        { Prefix + "/link", "/outside" },
    };

    /// <summary>The archive size and digest are checked against the pin before extraction.</summary>
    [Fact]
    public void PinnedArchiveIsCheckedBeforeExtraction()
    {
        using var directory = new TemporaryDirectory();
        string archive = Path.Combine(directory.Path, "archive.tar.gz");
        File.WriteAllBytes(archive, "wrong"u8.ToArray());
        MamePin pin = Pin(5, new string('0', 64));
        Assert.Contains("digest mismatch", SourceArchive.Validate(archive, pin)?.Message, StringComparison.Ordinal);
        Assert.Contains("digest mismatch", SourceArchive.Extract(archive, Path.Combine(directory.Path, "output"), pin).Failure.Message, StringComparison.Ordinal);
        Assert.Contains("size mismatch", SourceArchive.Validate(archive, Pin(6, new string('0', 64)))?.Message, StringComparison.Ordinal);
    }

    /// <summary>Regular files and internal links are extracted; the epoch is the first member's modification time.</summary>
    [Fact]
    public void SafeArchivePreservesRegularFilesAndInternalLinks()
    {
        using var directory = new TemporaryDirectory();
        string archive = Archive(directory.Path, FileEntry(Prefix + "/file", "bytes", 12345), LinkEntry(Prefix + "/link", "file"));
        ExtractedSource source = SourceArchive.Extract(archive, Path.Combine(directory.Path, "output"), PinFor(archive)).Value;
        Assert.Equal("bytes"u8.ToArray(), File.ReadAllBytes(Path.Combine(source.Source, "link")));
        Assert.Equal("file", new FileInfo(Path.Combine(source.Source, "link")).LinkTarget);
        Assert.Equal(12345, source.Epoch);
    }

    /// <summary>Traversal, absolute names, foreign prefixes and escaping links are unsafe.</summary>
    [Theory]
    [MemberData(nameof(UnsafeMembers))]
    public void PathTraversalAndExternalLinksAreRejected(string name, string? link)
    {
        TarEntry entry = link is null ? new PaxTarEntry(TarEntryType.RegularFile, name) : new PaxTarEntry(TarEntryType.SymbolicLink, name) { LinkName = link };
        Assert.Contains("Unsafe", SourceArchive.ValidateMembers([entry], Prefix).Failure.Message, StringComparison.Ordinal);
    }

    /// <summary>FIFOs, devices and hard links are unsupported.</summary>
    [Theory]
    [InlineData(TarEntryType.Fifo)]
    [InlineData(TarEntryType.CharacterDevice)]
    [InlineData(TarEntryType.BlockDevice)]
    [InlineData(TarEntryType.HardLink)]
    public void SpecialFilesAndHardLinksAreRejected(TarEntryType kind)
    {
        var entry = new PaxTarEntry(kind, Prefix + "/device");
        if (kind == TarEntryType.HardLink)
            entry.LinkName = "file";
        Assert.Contains("Unsupported", SourceArchive.ValidateMembers([entry], Prefix).Failure.Message, StringComparison.Ordinal);
    }

    /// <summary>Every member is validated before anything is written, so an unsafe last member leaves no output.</summary>
    [Fact]
    public void UnsafeLastMemberPreventsAnyExtraction()
    {
        using var directory = new TemporaryDirectory();
        string archive = Archive(directory.Path, DirectoryEntry(Prefix + "/", 1), FileEntry(Prefix + "/file", "bytes", 1), FileEntry(Prefix + "/../escape", "bytes", 1));
        string destination = Path.Combine(directory.Path, "output");
        Assert.Contains("Unsafe archive member", SourceArchive.Extract(archive, destination, PinFor(archive)).Failure.Message, StringComparison.Ordinal);
        Assert.False(Path.Exists(destination));
    }

    /// <summary>Duplicate members and members beneath an archived symlink cannot redirect writes.</summary>
    [Fact]
    public void DuplicateMembersAndMembersBeneathLinksAreRejected()
    {
        Assert.Contains("Duplicate archive member", SourceArchive.ValidateMembers(
            [new PaxTarEntry(TarEntryType.RegularFile, Prefix + "/file"), new PaxTarEntry(TarEntryType.RegularFile, Prefix + "/./file")], Prefix).Failure.Message,
            StringComparison.Ordinal);
        Assert.Contains("Unsafe archive member", SourceArchive.ValidateMembers(
            [new PaxTarEntry(TarEntryType.RegularFile, Prefix + "/link/file"), LinkEntry(Prefix + "/link", "directory")], Prefix).Failure.Message,
            StringComparison.Ordinal);
    }

    /// <summary>A pax global header is metadata: it is skipped for the epoch and may carry only git's commit comment.</summary>
    [Fact]
    public void PaxGlobalHeaderIsNotAMember()
    {
        using var directory = new TemporaryDirectory();
        var comment = new PaxGlobalExtendedAttributesTarEntry(new Dictionary<string, string>(StringComparer.Ordinal) { ["comment"] = new string('a', 40) });
        string archive = Archive(directory.Path, comment, DirectoryEntry(Prefix + "/", 777), FileEntry(Prefix + "/file", "bytes", 12345));
        Assert.Equal(777, SourceArchive.Extract(archive, Path.Combine(directory.Path, "output"), PinFor(archive)).Value.Epoch);
        var path = new PaxGlobalExtendedAttributesTarEntry(new Dictionary<string, string>(StringComparer.Ordinal) { ["path"] = "elsewhere" });
        Assert.Contains("Unsupported archive member", SourceArchive.ValidateMembers([path], Prefix).Failure.Message, StringComparison.Ordinal);
    }

    /// <summary>Extraction applies Python's data-filter modes and the recorded modification times.</summary>
    [Fact]
    public void ExtractionAppliesDataFilterModesAndTimes()
    {
        if (OperatingSystem.IsWindows())
            return;
        using var directory = new TemporaryDirectory();
        string archive = Archive(directory.Path, DirectoryEntry(Prefix + "/", 2000), FileEntry(Prefix + "/tool", "#!", 3000, (UnixFileMode)0b111_111_101),
            FileEntry(Prefix + "/data", "data", 4000, (UnixFileMode)0b100_100_100));
        string source = SourceArchive.Extract(archive, Path.Combine(directory.Path, "output"), PinFor(archive)).Value.Source;
        Assert.Equal((UnixFileMode)0b111_101_101, File.GetUnixFileMode(Path.Combine(source, "tool")));
        Assert.Equal((UnixFileMode)0b110_100_100, File.GetUnixFileMode(Path.Combine(source, "data")));
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(3000).UtcDateTime, File.GetLastWriteTimeUtc(Path.Combine(source, "tool")));
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(2000).UtcDateTime, Directory.GetLastWriteTimeUtc(source));
    }

    /// <summary>Data-filter modes drop special and group/other write bits and keep execute only with user execute.</summary>
    [Theory]
    [InlineData(0b110_110_100, 0b110_100_100)]
    [InlineData(0b111_111_101, 0b111_101_101)]
    [InlineData(0b100_111_111_111, 0b111_101_101)]
    [InlineData(0b110_001_001, 0b110_000_000)]
    [InlineData(0b100_100_100, 0b110_100_100)]
    public void DataModesMatchPythonTarfile(int mode, int expected) =>
        Assert.Equal((UnixFileMode)expected, SourceArchive.DataMode((UnixFileMode)mode));

    /// <summary>Generation keeps fatal warnings and bundled codecs.</summary>
    [Fact]
    public void GenerationKeepsFatalWarningsAndBundledCodecs()
    {
        IReadOnlyList<string> arguments = ChdmanBuild.GenerateArguments("/source", "21.0.0");
        Assert.DoesNotContain("--with-emulator", arguments);
        Assert.DoesNotContain(arguments, value => value.Contains("NOWERROR", StringComparison.Ordinal) || value.Contains("with-system", StringComparison.Ordinal));
        Assert.Contains("--STRIP_SYMBOLS=1", arguments);
        Assert.Contains("--osd=mac", arguments);
        Assert.Contains("--gcc_version=21.0.0", arguments);
        Assert.Equal(Path.GetFullPath("/source/3rdparty/genie/bin/darwin/genie"), Path.GetFullPath(arguments[0]));
        Assert.Contains("--ARCHOPTS=-ffile-prefix-map=/source=/_/mame -mmacosx-version-min=14.0", arguments);
        Assert.Contains(ChdmanBuild.LinkArguments(8), value => value.Contains("-fatal_warnings,-reproducible", StringComparison.Ordinal));
        Assert.Contains("-j8", ChdmanBuild.LinkArguments(8));
    }

    /// <summary>Make variables in the shell cannot change the recorded recipe.</summary>
    [Fact]
    public void MakeEnvironmentCannotChangeTheRecordedRecipe()
    {
        IReadOnlyDictionary<string, string> environment = ChdmanBuild.MakeEnvironment(Environment(("PATH", "/tools"), ("OPTIMIZE", "0"), ("NOWERROR", "1")), 12345).Value;
        Assert.Equal(Environment(("PATH", "/tools"), ("SOURCE_DATE_EPOCH", "12345"), ("LC_ALL", "C"), ("TZ", "UTC")), environment);
        Assert.Contains("override", ChdmanBuild.MakeEnvironment(Environment(("MAKEFILES", "/unsafe/file")), 12345).Failure.Message, StringComparison.Ordinal);
    }

    /// <summary>Each make and GENie override is rejected rather than dropped.</summary>
    [Theory]
    [InlineData("MAKEFLAGS")]
    [InlineData("MFLAGS")]
    [InlineData("GENIE_FLAGS")]
    [InlineData("MAKEFILES")]
    [InlineData("MAKEOVERRIDES")]
    [InlineData("GNUMAKEFLAGS")]
    public void MakeOverridesAreRejected(string name) =>
        Assert.Contains("make/GENie environment override", ChdmanBuild.MakeEnvironment(Environment(("PATH", "/tools"), (name, "-j1")), 1).Failure.Message, StringComparison.Ordinal);

    /// <summary>The recipe digest hashes the <c>shasum -a 256</c> listing of every recipe source in ordinal path order.</summary>
    [Fact]
    public void RecipeDigestCoversEveryRecipeSourceInOrdinalOrder()
    {
        using var directory = new TemporaryDirectory();
        string recipe = Path.Combine(directory.Path, ChdmanBuild.RecipeDirectory);
        Directory.CreateDirectory(Path.Combine(recipe, "Nested"));
        File.WriteAllText(Path.Combine(recipe, "a.cs"), "a");
        File.WriteAllText(Path.Combine(recipe, "B.cs"), "B");
        File.WriteAllText(Path.Combine(recipe, "Nested", "c.cs"), "c");
        File.WriteAllText(Path.Combine(recipe, "notes.txt"), "ignored");
        // Ordinal, not culture, order: uppercase sorts before lowercase.
        string[] expected = [ChdmanBuild.RecipeDirectory + "/B.cs", ChdmanBuild.RecipeDirectory + "/Nested/c.cs", ChdmanBuild.RecipeDirectory + "/a.cs"];
        Assert.Equal(expected, ChdmanBuild.RecipeSources(directory.Path));
        string listing = $"{Sha256("B")}  {expected[0]}\n{Sha256("c")}  {expected[1]}\n{Sha256("a")}  {expected[2]}\n";
        Assert.Equal(Sha256(listing), ChdmanBuild.RecipeSha256(directory.Path));
    }

    /// <summary>The real recipe identity covers exactly the chdman command's sources, in this order.</summary>
    [Fact]
    public void RepositoryRecipeCoversTheChdmanSources() =>
        Assert.Equal(
            ["ChdmanBuild.cs", "ChdmanCommand.cs", "ChdmanSdlHeaders.cs", "MamePin.cs", "ReceiptJson.cs", "SourceArchive.cs"],
            ChdmanBuild.RecipeSources(TestRepository.Root).Select(path => path[(ChdmanBuild.RecipeDirectory.Length + 1)..]));

    /// <summary>Receipts use Python's <c>json.dumps(indent=2)</c> layout.</summary>
    [Fact]
    public void ReceiptJsonMatchesPythonLayout()
    {
        var receipt = new JsonObject
        {
            ["attestation"] = null,
            ["empty"] = new JsonArray(),
            ["nested"] = new JsonArray(new JsonArray(), new JsonObject()),
            ["compiler"] = "clang\nTarget: arm64 + \"q\" \\ <&>",
            ["bytes"] = 12345,
        };
        const string expected = "{\n  \"attestation\": null,\n  \"empty\": [],\n  \"nested\": [\n    [],\n    {}\n  ],\n"
            + "  \"compiler\": \"clang\\nTarget: arm64 + \\\"q\\\" \\\\ <&>\",\n  \"bytes\": 12345\n}\n";
        Assert.Equal(expected, ReceiptJson.Serialize(receipt));
    }

    /// <summary>The checked-in MAME pin satisfies the recipe's pin checks.</summary>
    [Fact]
    public void CurrentPinIsAccepted()
    {
        MamePin pin = MamePin.Read(TestRepository.Root).Value;
        Assert.Equal("mame-mame0289", pin.ArchivePrefix);
        Assert.Equal(213237316, pin.SourceBytes);
    }

    /// <summary>Pin drift fails closed with the Python messages.</summary>
    [Theory]
    [InlineData("repository", "\"https://example.invalid/mame\"", "Unexpected MAME repository")]
    [InlineData("sourceSha256", "\"ABC\"", "Invalid archive digest")]
    [InlineData("commit", "\"f34f025\"", "Invalid source commit")]
    [InlineData("sourceBytes", "0", "Invalid source size/revision")]
    [InlineData("rebuildRevision", "0", "Invalid source size/revision")]
    [InlineData("version", "\"0.290\"", "Tag/version mismatch")]
    public void PinDriftIsRejected(string field, string json, string message)
    {
        using var directory = new TemporaryDirectory();
        TestRepository.CopyTo(directory.Path, MamePin.PinPath);
        string path = Path.Combine(directory.Path, MamePin.PinPath);
        JsonObject pin = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        pin[field] = JsonNode.Parse(json);
        File.WriteAllText(path, pin.ToJsonString());
        Assert.Contains(message, MamePin.Read(directory.Path).Failure.Message, StringComparison.Ordinal);
    }

    /// <summary>Without --output, chdman build targets this host's RID directory, never a fixed platform.</summary>
    [Fact]
    public void DefaultOutputIsTheHostRidDirectory()
    {
        using var directory = new TemporaryDirectory();
        TestRepository.CopyTo(directory.Path, MamePin.PinPath);
        using var output = new StringWriter();
        using var error = new StringWriter();
        int exit = ChdmanCommand.Build(["--archive", Path.Combine(directory.Path, "missing.tar.gz"), "--log", Path.Combine(directory.Path, "build.log")],
            new CommandContext(directory.Path, output, error, TestRepository.CleanEnvironment));
        Assert.Equal(1, exit);
        Assert.Contains("Archive must be a regular file", error.ToString(), StringComparison.Ordinal);
        string rid = ChdmanBuild.HostRid().Value;
        Assert.Equal("artifacts/tools/chdman/" + rid, ChdmanCommand.DefaultOutput(rid));
        Assert.True(Directory.Exists(Path.Combine(directory.Path, "artifacts", "tools", "chdman", rid)));
    }

    private static MamePin Pin(long bytes, string sha256) => new(new JsonObject(), "mame000", new string('0', 40), "0.00", bytes, sha256);

    private static MamePin PinFor(string archive) => Pin(new FileInfo(archive).Length, Digest.Sha256File(archive));

    private static PaxTarEntry FileEntry(string name, string content, long mtime, UnixFileMode mode = (UnixFileMode)0b110_100_100) =>
        new(TarEntryType.RegularFile, name)
        {
            DataStream = new MemoryStream(Encoding.ASCII.GetBytes(content)),
            ModificationTime = DateTimeOffset.FromUnixTimeSeconds(mtime),
            Mode = mode,
        };

    private static PaxTarEntry DirectoryEntry(string name, long mtime) =>
        new(TarEntryType.Directory, name) { ModificationTime = DateTimeOffset.FromUnixTimeSeconds(mtime) };

    private static PaxTarEntry LinkEntry(string name, string target) => new(TarEntryType.SymbolicLink, name) { LinkName = target };

    private static string Archive(string directory, params TarEntry[] entries)
    {
        string path = Path.Combine(directory, "archive.tar.gz");
        using (FileStream file = File.Create(path))
        using (var gzip = new GZipStream(file, CompressionLevel.Fastest))
        using (var writer = new TarWriter(gzip, TarEntryFormat.Pax))
        {
            foreach (TarEntry entry in entries)
                writer.WriteEntry(entry);
        }
        return path;
    }

    private static Dictionary<string, string> Environment(params (string Name, string Value)[] values) =>
        values.ToDictionary(value => value.Name, value => value.Value, StringComparer.Ordinal);

    private static string Sha256(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
}
