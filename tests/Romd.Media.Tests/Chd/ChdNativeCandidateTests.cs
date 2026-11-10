using System.Reflection;
using System.Runtime.Loader;
using System.Xml.Linq;
using Romd.Media.Chd;
using Romd.Media.Inspection;
using Romd.Media.TestSupport;
using Shouldly;
using Xunit;

namespace Romd.Media.Tests.Chd;

public sealed class ChdNativeCandidateTests : IDisposable
{
    private static readonly byte[] SyntheticBinary = [1, 2, 3, 4];
    private readonly string _directory = Directory.CreateTempSubdirectory("romd-native-candidate-").FullName;

    [Fact]
    public void Constructor_MissingDirectory_DoesNotOpenOrLoad()
    {
        string absent = Path.Combine(_directory, "absent");
        var candidate = new ChdNativeCandidate(absent, PrototypeIdentity());

        candidate.ExpectedIdentity.ShouldBe(PrototypeIdentity());
        candidate.ManifestPath.ShouldBe(Path.Combine(absent, "build-manifest.json"));
        candidate.BinaryPath.ShouldBe(Path.Combine(absent, "native", PrototypeIdentity().File));
        Directory.EnumerateFileSystemEntries(_directory).ShouldBeEmpty();
    }

    [Theory]
    [InlineData("relative-directory")]
    [InlineData("zero-binary-size")]
    [InlineData("invalid-sha256")]
    [InlineData("missing-crc")]
    [InlineData("zero-recipe")]
    [InlineData("parent-file")]
    [InlineData("rooted-file")]
    [InlineData("nested-file")]
    [InlineData("uppercase-rid")]
    public void Constructor_InvalidCandidate_RejectsBeforeFileAccess(string mutation)
    {
        ChdNativeIdentity identity = PrototypeIdentity();
        string directory = Path.Combine(_directory, "build");
        switch (mutation)
        {
            case "relative-directory":
                directory = "build";
                break;
            case "zero-binary-size":
                identity = identity with { BinaryBytes = 0 };
                break;
            case "invalid-sha256":
                identity = identity with { BinarySha256 = "wrong" };
                break;
            case "missing-crc":
                identity = identity with { VerifiesBlockCrc = false };
                break;
            case "zero-recipe":
                identity = identity with { RecipeVersion = 0 };
                break;
            case "parent-file":
                identity = identity with { File = "../libmoonlark_chdr.so" };
                break;
            case "rooted-file":
                identity = identity with { File = "/usr/lib/libmoonlark_chdr.so" };
                break;
            case "nested-file":
                identity = identity with { File = "native/libmoonlark_chdr.so" };
                break;
            case "uppercase-rid":
                identity = identity with { Rid = "Linux-X64" };
                break;
        }

        Should.Throw<ArgumentException>(() => new ChdNativeCandidate(directory, identity));
        Directory.EnumerateFileSystemEntries(_directory).ShouldBeEmpty();
    }

    [Theory]
    [InlineData("BinaryMissing")]
    [InlineData("BinarySizeMismatch")]
    [InlineData("BinarySha256Mismatch")]
    [InlineData("ManifestMissing")]
    [InlineData("ManifestInvalid")]
    [InlineData("ManifestDuplicateProperty")]
    [InlineData("ManifestWrongSchema")]
    [InlineData("ManifestIdentityMismatch")]
    [InlineData("ManifestPinMismatch")]
    public void VerifyAndLoad_InvalidBuild_ReturnsPreciseVerificationCauseBeforeNativeLoad(string cause)
    {
        string build = SyntheticBuild();
        var candidate = ChdNativeCandidate.FromBuildRecord(build);
        string record = candidate.ManifestPath;
        switch (cause)
        {
            case "BinaryMissing":
                File.Delete(candidate.BinaryPath);
                break;
            case "BinarySizeMismatch":
                File.WriteAllBytes(candidate.BinaryPath, [1, 2, 3, 4, 5]);
                break;
            case "BinarySha256Mismatch":
                File.WriteAllBytes(candidate.BinaryPath, [0, 2, 3, 4]);
                break;
            case "ManifestMissing":
                File.Delete(record);
                break;
            case "ManifestInvalid":
                File.WriteAllBytes(record, "not-json"u8.ToArray());
                break;
            case "ManifestDuplicateProperty":
                File.WriteAllText(record, File.ReadAllText(record).Replace(
                "\"schemaVersion\":3", "\"schemaVersion\":3,\"schemaVersion\":3", StringComparison.Ordinal));
                break;
            case "ManifestWrongSchema":
                NativeTestBuilds.ChangeRecord(build, node => node["schemaVersion"] = 2);
                break;
            case "ManifestIdentityMismatch":
                NativeTestBuilds.ChangeRecord(build, node => node["recipeVersion"] = 2);
                break;
            case "ManifestPinMismatch":
                NativeTestBuilds.ChangeRecord(build, node =>
            {
                node["upstreamCommit"] = new string('1', 40);
                node["buildInfo"]!["upstreamCommit"] = new string('1', 40);
            });
                break;
        }

        var rejected = Should.Throw<ChdNativeCandidateException>(() => candidate.VerifyAndLoad());

        rejected.Status.ShouldBe(MediaWorkerStatus.Malformed);
        rejected.Rejection.Stage.ShouldBe(ChdRejectionStage.NativeVerification);
        rejected.Rejection.Cause.ShouldBe(cause is "ManifestDuplicateProperty" or "ManifestWrongSchema" ? "ManifestInvalid" : cause);
        rejected.Rejection.Path.ShouldBe(cause.StartsWith("Binary", StringComparison.Ordinal) ? candidate.BinaryPath : record);
        rejected.Rejection.NativeError.ShouldBeNull();
    }

    [Fact]
    public void VerifyAndLoad_RecordForAnotherRid_RejectsBeforeBinaryAccess()
    {
        string build = SyntheticBuild(rid: NativeTestBuilds.Rid == "linux-arm64" ? "osx-arm64" : "linux-arm64");
        var candidate = ChdNativeCandidate.FromBuildRecord(build);
        File.Delete(candidate.BinaryPath);

        var rejected = Should.Throw<ChdNativeCandidateException>(() => candidate.VerifyAndLoad());

        rejected.Status.ShouldBe(MediaWorkerStatus.Unsupported);
        rejected.Rejection.Cause.ShouldBe("ProcessRidMismatch");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void VerifyAndLoad_LinkedBinaryOrNativeDirectory_RejectsBeforeHashingOrLoading(bool linkDirectory)
    {
        string real = SyntheticBuild("real");
        string linked = Path.Combine(_directory, "linked");
        Directory.CreateDirectory(linked);
        File.Copy(Path.Combine(real, "build-manifest.json"), Path.Combine(linked, "build-manifest.json"));
        var candidate = ChdNativeCandidate.FromBuildRecord(linked);
        if (linkDirectory)
            Directory.CreateSymbolicLink(Path.Combine(linked, "native"), Path.Combine(real, "native"));
        else
        {
            Directory.CreateDirectory(Path.Combine(linked, "native"));
            File.CreateSymbolicLink(candidate.BinaryPath, Path.Combine(real, "native", candidate.ExpectedIdentity.File));
        }

        var rejected = Should.Throw<ChdNativeCandidateException>(() => candidate.VerifyAndLoad());

        rejected.Rejection.Cause.ShouldBe("BinaryNotRegularFile");
        rejected.Rejection.Path.ShouldBe(candidate.BinaryPath);
    }

    [Theory]
    [InlineData("/usr/lib/libSystem.B.dylib")]
    [InlineData("/lib/x86_64-linux-gnu/libz.so.1")]
    [InlineData("../libmoonlark_chdr.so")]
    [InlineData("native/libmoonlark_chdr.so")]
    public void FromBuildRecord_SystemOrOutsideBinary_RefusesTheRecord(string file)
    {
        string build = SyntheticBuild();
        NativeTestBuilds.ChangeRecord(build, node => node["file"] = file);

        var rejected = Should.Throw<ChdNativeCandidateException>(() => ChdNativeCandidate.FromBuildRecord(build));

        rejected.Rejection.Cause.ShouldBe("ManifestInvalid");
        rejected.Rejection.Path.ShouldBe(Path.Combine(build, "build-manifest.json"));
    }

    [Fact]
    public void FromBuildRecord_RelativeDirectory_RefusesBeforeFileAccess() =>
        Should.Throw<ArgumentException>(() => ChdNativeCandidate.FromBuildRecord(Path.Combine("artifacts", "native", "libchdr")));

    [Fact]
    public void VerifyAndLoad_Cancelled_PropagatesWithoutOpeningMissingFiles()
    {
        var candidate = new ChdNativeCandidate(Path.Combine(_directory, "missing"), PrototypeIdentity());

        Should.Throw<OperationCanceledException>(() => candidate.VerifyAndLoad(new CancellationToken(canceled: true)));
    }

    [Fact]
    public void VerifyAndLoad_RepositoryBuild_ReturnsExactlyRecordedResidentIdentity()
    {
        ChdNativeCandidate candidate = RepositoryNative();

        ChdNativeIdentity actual = candidate.VerifyAndLoad();

        actual.ShouldBe(candidate.ExpectedIdentity);
        actual.Rid.ShouldBe(NativeRuntimeIdentifier.Current);
        actual.UpstreamCommit.ShouldBe(ChdNativeCandidate.CompiledUpstreamCommit);
        actual.RecipeVersion.ShouldBeGreaterThanOrEqualTo(1);
        actual.HasRawSectors.ShouldBeTrue();
        actual.HasSubcode.ShouldBeTrue();
        actual.VerifiesBlockCrc.ShouldBeTrue();
    }

    [Fact]
    public void CompiledUpstreamCommit_IsThePinnedLibchdrSubmoduleCommit()
    {
        var props = XDocument.Load(Path.Combine(NativeTestBuilds.RepositoryRoot, "native", "libchdr", "libchdr.props"));

        ChdNativeCandidate.CompiledUpstreamCommit.ShouldBe(props.Descendants("LibchdrUpstreamCommit").Single().Value);
    }

    [Fact]
    public void VerifyAndLoad_RecordAndExpectedAgreeOnFalseBuildId_RejectsActualResident()
    {
        string copy = NativeTestBuilds.CopyBuild(NativeTestBuilds.LibchdrDirectory, Path.Combine(_directory, "false-build"));
        string realBuild = RepositoryNative().ExpectedIdentity.BuildId;
        NativeTestBuilds.ChangeRecord(copy, node => node["buildInfo"]!["buildId"] = new string(realBuild[0] == 'f' ? 'e' : 'f', 64));

        // The copy loads into a fresh load context, so this process's verified build stays the only resident one.
        (string cause, string path) = IsolatedVerifyAndLoad(copy);

        cause.ShouldBe("ResidentBuildMismatch");
        path.ShouldBe(Path.Combine(copy, "native", RepositoryNative().ExpectedIdentity.File));
    }

    internal static ChdNativeCandidate RepositoryNative() => ChdNativeCandidate.FromBuildRecord(NativeTestBuilds.LibchdrDirectory);

    internal static ChdNativeIdentity PrototypeIdentity() => new(NativeTestBuilds.Rid, "libmoonlark_chdr.so", 4, new string('a', 64),
        ChdNativeCandidate.CompiledUpstreamCommit, "v0.3.0-test", 1, "1.0.0-preview.1", new string('e', 64), true, true, true);

    private string SyntheticBuild(string name = "build", string? rid = null) =>
        NativeTestBuilds.WriteLibchdrBuild(Path.Combine(_directory, name), SyntheticBinary, rid ?? NativeTestBuilds.Rid,
            ChdNativeCandidate.CompiledUpstreamCommit);

    private static (string Cause, string Path) IsolatedVerifyAndLoad(string buildDirectory)
    {
        var context = new IsolatedMediaContext();
        try
        {
            Type candidateType = context.LoadFromAssemblyName(new AssemblyName("Romd.Media"))
                .GetType("Romd.Media.Chd.ChdNativeCandidate", throwOnError: true)!;
            object candidate = candidateType.GetMethod(nameof(ChdNativeCandidate.FromBuildRecord))!.Invoke(null, [buildDirectory])!;
            var error = Should.Throw<TargetInvocationException>(() =>
                candidateType.GetMethod(nameof(ChdNativeCandidate.VerifyAndLoad))!.Invoke(candidate, [CancellationToken.None]));
            object rejection = error.InnerException!.GetType().GetProperty(nameof(ChdNativeCandidateException.Rejection))!
                .GetValue(error.InnerException)!;
            return ((string)rejection.GetType().GetProperty(nameof(ChdRejection.Cause))!.GetValue(rejection)!,
                (string)rejection.GetType().GetProperty(nameof(ChdRejection.Path))!.GetValue(rejection)!);
        }
        finally { context.Unload(); }
    }

    private sealed class IsolatedMediaContext() : AssemblyLoadContext("romd-native-isolation", isCollectible: true)
    {
        protected override Assembly? Load(AssemblyName name) => name.Name is "Romd.Media" or "Moonlark.Libchdr"
            ? LoadFromAssemblyPath(Path.Combine(AppContext.BaseDirectory, name.Name + ".dll"))
            : null;
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);
}
