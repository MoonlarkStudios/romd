using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Romd.Media.Inspection;
using Romd.Media.PlayStation;
using Romd.Media.TestSupport;
using Shouldly;
using Xunit;

namespace Romd.Media.Tests.PlayStation;

public sealed class ChdmanCandidateTests : IDisposable
{
    // These four bytes are declared synthetic data, never an executable or historical tool; verification never runs it.
    private static readonly byte[] SyntheticBinary = [1, 2, 3, 4];
    private readonly string _directory = Directory.CreateTempSubdirectory("romd-chdman-candidate-").FullName;

    [Fact]
    public void Constructor_MissingDirectory_DoesNoIo()
    {
        string absent = Path.Combine(_directory, "absent");
        var candidate = new ChdmanCandidate(absent, Prototype());

        candidate.ExpectedIdentity.ShouldBe(Prototype());
        candidate.BinaryPath.ShouldBe(Path.Combine(absent, "native", "chdman"));
        candidate.ManifestPath.ShouldBe(Path.Combine(absent, "build-manifest.json"));
        Directory.EnumerateFileSystemEntries(_directory).ShouldBeEmpty();
    }

    [Theory]
    [InlineData("relative-directory")]
    [InlineData("control-path")]
    [InlineData("oversized-path")]
    [InlineData("zero-binary")]
    [InlineData("oversized-binary")]
    [InlineData("bad-binary-hash")]
    [InlineData("zero-binary-hash")]
    [InlineData("bad-source-hash")]
    [InlineData("zero-source-hash")]
    [InlineData("bad-tag")]
    [InlineData("bad-rid")]
    [InlineData("zero-recipe")]
    public void Constructor_InvalidTuple_RejectsWithoutFileAccess(string mutation)
    {
        string directory = Path.Combine(_directory, "build");
        ChdmanToolIdentity expected = Prototype();
        switch (mutation)
        {
            case "relative-directory":
                directory = "build";
                break;
            case "control-path":
                directory += "\n";
                break;
            case "oversized-path":
                directory = Path.Combine(_directory, new string('a', 1025));
                break;
            case "zero-binary":
                expected = expected with { BinaryBytes = 0 };
                break;
            case "oversized-binary":
                expected = expected with { BinaryBytes = (64L << 20) + 1 };
                break;
            case "bad-binary-hash":
                expected = expected with { BinarySha256 = "wrong" };
                break;
            case "zero-binary-hash":
                expected = expected with { BinarySha256 = new string('0', 64) };
                break;
            case "bad-source-hash":
                expected = expected with { MameSourceSha256 = new string('A', 64) };
                break;
            case "zero-source-hash":
                expected = expected with { MameSourceSha256 = new string('0', 64) };
                break;
            case "bad-tag":
                expected = expected with { MameTag = "mame 0289" };
                break;
            case "bad-rid":
                expected = expected with { Rid = "Linux-X64" };
                break;
            case "zero-recipe":
                expected = expected with { RecipeVersion = 0 };
                break;
        }

        Should.Throw<ArgumentException>(() => new ChdmanCandidate(directory, expected));
        Directory.EnumerateFileSystemEntries(_directory).ShouldBeEmpty();
    }

    [Fact]
    public void Verify_Cancelled_PropagatesBeforeMissingFileAccess()
    {
        var candidate = new ChdmanCandidate(Path.Combine(_directory, "missing"), Prototype());

        Should.Throw<OperationCanceledException>(() => candidate.Verify(new CancellationToken(canceled: true)));
    }

    [Fact]
    public void Verify_DifferentHostRid_RejectsBeforeMissingFileAccess()
    {
        var expected = Prototype() with { Rid = HostRid() == "linux-arm64" ? "linux-x64" : "linux-arm64" };
        var candidate = new ChdmanCandidate(Path.Combine(_directory, "missing"), expected);

        AssertRejected(candidate, "ProcessRidMismatch", candidate.BinaryPath, MediaWorkerStatus.Unsupported);
    }

    [Fact]
    public void Verify_RecordedSyntheticBuild_BindsMeasuredBytesToTheRecord()
    {
        var candidate = Create();
        if (AssertUnsupportedHost(candidate))
            return;

        ChdmanToolIdentity verified = candidate.Verify();

        verified.ShouldBe(candidate.ExpectedIdentity);
        verified.MameTag.ShouldBe(ChdmanPin.Tag);
        verified.MameSourceSha256.ShouldBe(ChdmanPin.SourceSha256);
        candidate.Matches(verified).ShouldBeTrue();
        candidate.Matches(verified with { RecipeVersion = verified.RecipeVersion + 1 }).ShouldBeFalse();
    }

    [Theory]
    [InlineData("BinaryMissing", "BinaryMissing")]
    [InlineData("BinaryLengthChanged", "BinarySizeMismatch")]
    [InlineData("BinaryContentsChangedSameLength", "BinarySha256Mismatch")]
    [InlineData("BinaryDirectory", "BinaryNotRegularFile")]
    [InlineData("BinaryLink", "BinaryNotRegularFile")]
    [InlineData("NativeDirectoryLink", "BinaryNotRegularFile")]
    [InlineData("ManifestMissing", "ManifestMissing")]
    [InlineData("ManifestOversized", "ManifestInvalid")]
    public void Verify_ActualFileMutation_ReturnsPreciseCauseWithOriginalTuple(string mutation, string cause)
    {
        var candidate = Create();
        ChdmanToolIdentity original = candidate.ExpectedIdentity;
        string native = Path.Combine(candidate.BuildDirectory, "native");
        switch (mutation)
        {
            case "BinaryMissing":
                File.Delete(candidate.BinaryPath);
                break;
            case "BinaryLengthChanged":
                File.WriteAllBytes(candidate.BinaryPath, [1, 2, 3, 4, 5]);
                break;
            case "BinaryContentsChangedSameLength":
                File.WriteAllBytes(candidate.BinaryPath, [0, 2, 3, 4]);
                break;
            case "BinaryDirectory":
                File.Delete(candidate.BinaryPath);
                Directory.CreateDirectory(candidate.BinaryPath);
                break;
            case "BinaryLink":
                string target = Path.Combine(_directory, "elsewhere-chdman");
                File.Move(candidate.BinaryPath, target);
                File.CreateSymbolicLink(candidate.BinaryPath, target);
                break;
            case "NativeDirectoryLink":
                string moved = Path.Combine(_directory, "elsewhere-native");
                Directory.Move(native, moved);
                Directory.CreateSymbolicLink(native, moved);
                break;
            case "ManifestMissing":
                File.Delete(candidate.ManifestPath);
                break;
            case "ManifestOversized":
                File.WriteAllBytes(candidate.ManifestPath, new byte[(1 << 20) + 1]);
                break;
        }
        candidate.ExpectedIdentity.ShouldBe(original);
        if (AssertUnsupportedHost(candidate))
            return;

        string path = mutation.StartsWith("Manifest", StringComparison.Ordinal) ? candidate.ManifestPath : candidate.BinaryPath;
        AssertRejected(candidate, cause, path);
    }

    [Theory]
    [InlineData("rid")]
    [InlineData("recipe")]
    [InlineData("binary-bytes")]
    [InlineData("binary-sha")]
    public void Verify_RecordChangedAfterReading_RejectsTheIdentityMismatch(string mutation)
    {
        var candidate = Create();
        NativeTestBuilds.ChangeRecord(candidate.BuildDirectory, record =>
        {
            switch (mutation)
            {
                case "rid":
                    record["rid"] = "linux-other";
                    break;
                case "recipe":
                    record["recipeVersion"] = 2;
                    break;
                case "binary-bytes":
                    record["binary"]!["bytes"] = 5;
                    break;
                case "binary-sha":
                    record["binary"]!["sha256"] = new string('f', 64);
                    break;
            }
        });
        if (AssertUnsupportedHost(candidate))
            return;

        AssertRejected(candidate, "ManifestIdentityMismatch", candidate.ManifestPath);
    }

    [Theory]
    [InlineData("tag")]
    [InlineData("source")]
    public void Verify_RecordOfAnotherMameSource_RejectsThePinMismatch(string mutation)
    {
        string build = NativeTestBuilds.WriteChdmanBuild(Path.Combine(_directory, "foreign"), SyntheticBinary, HostRid() ?? "linux-x64",
            mutation == "tag" ? "mame0290" : ChdmanPin.Tag, mutation == "source" ? new string('1', 64) : ChdmanPin.SourceSha256);
        var candidate = ChdmanCandidate.FromBuildRecord(build);
        if (AssertUnsupportedHost(candidate))
            return;

        AssertRejected(candidate, "ManifestPinMismatch", candidate.ManifestPath);
    }

    [Theory]
    [InlineData("not-json")]
    [InlineData("duplicate-root")]
    [InlineData("duplicate-nested")]
    [InlineData("schema")]
    [InlineData("binary-path")]
    [InlineData("wrong-field-type")]
    [InlineData("too-deep")]
    public void Verify_MalformedRecord_RejectsBeforeAnyToolExecution(string mutation)
    {
        var candidate = Create();
        byte[] bytes = File.ReadAllBytes(candidate.ManifestPath);
        string text = Encoding.UTF8.GetString(bytes);
        switch (mutation)
        {
            case "not-json":
                bytes = "not-json"u8.ToArray();
                break;
            case "duplicate-root":
                bytes = Encoding.UTF8.GetBytes(text.Replace("\"schemaVersion\":2", "\"schemaVersion\":2,\"schemaVersion\":2", StringComparison.Ordinal));
                break;
            case "duplicate-nested":
                bytes = Encoding.UTF8.GetBytes(text.Replace("\"tag\":\"" + ChdmanPin.Tag + "\"",
                "\"tag\":\"" + ChdmanPin.Tag + "\",\"tag\":\"" + ChdmanPin.Tag + "\"", StringComparison.Ordinal));
                break;
            case "schema":
                bytes = Rewrite(bytes, record => record["schemaVersion"] = 1);
                break;
            case "binary-path":
                bytes = Rewrite(bytes, record => record["binary"]!["path"] = "../native/chdman");
                break;
            case "wrong-field-type":
                bytes = Rewrite(bytes, record => record["binary"]!["bytes"] = "4");
                break;
            case "too-deep":
                bytes = Rewrite(bytes, record =>
                {
                    JsonNode nested = new JsonObject();
                    for (int index = 0; index < 13; index++)
                        nested = new JsonArray(nested);
                    record["extra"] = nested;
                });
                break;
        }
        File.WriteAllBytes(candidate.ManifestPath, bytes);
        if (AssertUnsupportedHost(candidate))
            return;

        AssertRejected(candidate, "ManifestInvalid", candidate.ManifestPath);
    }

    [Fact]
    public void FromBuildRecord_RelativeDirectory_RefusesBeforeFileAccess() =>
        Should.Throw<ArgumentException>(() => ChdmanCandidate.FromBuildRecord(Path.Combine("artifacts", "tools", "chdman")));

    [Fact]
    public void Verify_RepositoryChdman_MatchesItsRecordAndRomdsMamePin()
    {
        var candidate = ChdmanCandidate.FromBuildRecord(NativeTestBuilds.ChdmanDirectory);

        ChdmanToolIdentity verified = candidate.Verify();

        verified.ShouldBe(candidate.ExpectedIdentity);
        verified.Rid.ShouldBe(NativeRuntimeIdentifier.Current);
        verified.MameTag.ShouldBe(ChdmanPin.Tag);
        verified.MameSourceSha256.ShouldBe(ChdmanPin.SourceSha256);
        verified.RecipeVersion.ShouldBeGreaterThanOrEqualTo(1);
    }

    [Fact]
    public void ChdmanPin_MatchesThePinnedMameArchive()
    {
        using var pin = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(NativeTestBuilds.RepositoryRoot, "native", "chdman", "mame.json")));

        pin.RootElement.GetProperty("tag").GetString().ShouldBe(ChdmanPin.Tag);
        pin.RootElement.GetProperty("sourceSha256").GetString().ShouldBe(ChdmanPin.SourceSha256);
    }

    private ChdmanCandidate Create() => ChdmanCandidate.FromBuildRecord(NativeTestBuilds.WriteChdmanBuild(
        Path.Combine(_directory, "build"), SyntheticBinary, HostRid() ?? "linux-x64", ChdmanPin.Tag, ChdmanPin.SourceSha256));

    private static byte[] Rewrite(byte[] bytes, Action<JsonNode> change)
    {
        JsonNode record = JsonNode.Parse(bytes)!;
        change(record);
        return JsonSerializer.SerializeToUtf8Bytes(record);
    }

    private static bool AssertUnsupportedHost(ChdmanCandidate candidate)
    {
        if (HostRid() == candidate.ExpectedIdentity.Rid)
            return false;
        AssertRejected(candidate, "ProcessRidMismatch", candidate.BinaryPath, MediaWorkerStatus.Unsupported);
        return true;
    }

    private static void AssertRejected(ChdmanCandidate candidate, string cause, string path, MediaWorkerStatus status = MediaWorkerStatus.Malformed)
    {
        var rejected = Should.Throw<ChdmanCandidateException>(() => candidate.Verify());
        rejected.Status.ShouldBe(status);
        rejected.Rejection.Stage.ShouldBe(ChdRejectionStage.NativeVerification);
        rejected.Rejection.Cause.ShouldBe(cause);
        rejected.Rejection.Path.ShouldBe(path);
        rejected.Rejection.CodecSlot.ShouldBeNull();
        rejected.Rejection.Codec.ShouldBeNull();
        rejected.Rejection.NativeError.ShouldBeNull();
    }

    private static ChdmanToolIdentity Prototype() => new(HostRid() ?? "linux-x64", ChdmanPin.Tag, ChdmanPin.SourceSha256, 1,
        4, new string('a', 64));

    private static string? HostRid() => RuntimeInformation.OSArchitecture == RuntimeInformation.ProcessArchitecture
        ? NativeRuntimeIdentifier.Current : null;

    public void Dispose() => Directory.Delete(_directory, recursive: true);
}
