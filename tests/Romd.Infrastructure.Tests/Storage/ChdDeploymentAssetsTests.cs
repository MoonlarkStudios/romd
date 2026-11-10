using Romd.Infrastructure.Storage;
using Romd.Media.Chd;
using Romd.Media.TestSupport;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Storage;

/// <summary>Configuration and build-record controls with synthetic records; no native load or tool execution.</summary>
public sealed class ChdDeploymentAssetsTests : IDisposable
{
    private const string Pin = "607694ca0812edfc9cc2030c64634fc2393668de";
    private readonly string _directory = Directory.CreateTempSubdirectory("romd-chd-deployment-").FullName;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Create_NoNativeDirectories_KeepsChdOff(bool requireTool) =>
        ChdDeploymentAssets.Create(new NativeToolOptions(), requireTool).ShouldBeNull();

    [Fact]
    public void Create_LibchdrOnly_ReadsItsRecordWithoutOpeningTheBinary()
    {
        string libchdr = Libchdr();
        File.Delete(Path.Combine(libchdr, "native", "libmoonlark_chdr.so"));

        var assets = ChdDeploymentAssets.Create(new() { LibchdrDirectory = libchdr }, requireTool: false).ShouldNotBeNull();

        assets.Native.BuildDirectory.ShouldBe(libchdr);
        assets.Native.ManifestPath.ShouldBe(Path.Combine(libchdr, "build-manifest.json"));
        assets.Native.BinaryPath.ShouldBe(Path.Combine(libchdr, "native", "libmoonlark_chdr.so"));
        assets.Native.ExpectedIdentity.ShouldBe(new ChdNativeIdentity("linux-arm64", "libmoonlark_chdr.so", 3,
            Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(new byte[] { 1, 2, 3 })), Pin,
            "v0.3.0-test", 1, "1.0.0-preview.1", new string('e', 64), true, true, true));
        assets.Tool.ShouldBeNull();
    }

    [Fact]
    public void Create_Worker_ReadsBothRecordsWithoutOpeningEitherBinary()
    {
        string libchdr = Libchdr(), chdman = Chdman();
        File.Delete(Path.Combine(chdman, "native", "chdman"));

        var assets = ChdDeploymentAssets.Create(new() { LibchdrDirectory = libchdr, ChdmanDirectory = chdman }, requireTool: true)
            .ShouldNotBeNull();

        var tool = assets.Tool.ShouldNotBeNull();
        tool.BuildDirectory.ShouldBe(chdman);
        tool.BinaryPath.ShouldBe(Path.Combine(chdman, "native", "chdman"));
        tool.ExpectedIdentity.ShouldBe(new ChdmanToolIdentity("linux-arm64", ChdmanPin.Tag, ChdmanPin.SourceSha256, 1, 3,
            Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(new byte[] { 4, 5, 6 }))));
    }

    [Fact]
    public void Create_ReadingHostWithConfiguredChdman_NeverReadsTheEncoder() =>
        ChdDeploymentAssets.Create(new() { LibchdrDirectory = Libchdr(), ChdmanDirectory = Path.Combine(_directory, "absent") },
            requireTool: false).ShouldNotBeNull().Tool.ShouldBeNull();

    [Fact]
    public void Create_WorkerWithoutChdman_FailsClosed() =>
        Should.Throw<InvalidOperationException>(() => ChdDeploymentAssets.Create(new() { LibchdrDirectory = Libchdr() }, requireTool: true))
            .Message.ShouldBe("Worker CHD encoding requires Romd:Native:ChdmanDirectory.");

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Create_ChdmanWithoutLibchdr_FailsClosed(bool requireTool) =>
        Should.Throw<InvalidOperationException>(() => ChdDeploymentAssets.Create(new() { ChdmanDirectory = Chdman() }, requireTool))
            .Message.ShouldBe("Romd:Native:ChdmanDirectory requires Romd:Native:LibchdrDirectory.");

    [Theory]
    [InlineData("libchdr", "")]
    [InlineData("libchdr", " ")]
    [InlineData("libchdr", "artifacts/native/libchdr/linux-x64")]
    [InlineData("libchdr", "relative")]
    [InlineData("chdman", "")]
    [InlineData("chdman", "artifacts/tools/chdman/linux-x64")]
    public void Create_BlankOrRelativeDirectory_FailsClosed(string key, string value)
    {
        var options = key == "libchdr" ? new NativeToolOptions { LibchdrDirectory = value, ChdmanDirectory = Chdman() }
            : new NativeToolOptions { LibchdrDirectory = Libchdr(), ChdmanDirectory = value };

        Should.Throw<InvalidOperationException>(() => ChdDeploymentAssets.Create(options, requireTool: true))
            .Message.ShouldEndWith("must be an absolute directory path.");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Create_MissingRecord_DoesNotSearchForAFallback(bool directoryExists)
    {
        string libchdr = Path.Combine(_directory, "empty");
        if (directoryExists)
            Directory.CreateDirectory(libchdr);

        Should.Throw<ChdNativeCandidateException>(() => ChdDeploymentAssets.Create(new() { LibchdrDirectory = libchdr }, false))
            .Rejection.Cause.ShouldBe("ManifestMissing");
    }

    [Theory]
    [InlineData("not-json")]
    [InlineData("schema")]
    [InlineData("system-file")]
    public void Create_MalformedLibchdrRecord_FailsClosed(string control)
    {
        string libchdr = Libchdr();
        string record = Path.Combine(libchdr, "build-manifest.json");
        if (control == "not-json")
            File.WriteAllText(record, "{");
        else
            NativeTestBuilds.ChangeRecord(libchdr, node =>
        {
            if (control == "schema")
                node["schemaVersion"] = 2;
            else
                node["file"] = "/usr/lib/libmoonlark_chdr.so";
        });

        Should.Throw<ChdNativeCandidateException>(() => ChdDeploymentAssets.Create(new() { LibchdrDirectory = libchdr }, false))
            .Rejection.Cause.ShouldBe("ManifestInvalid");
    }

    [Fact]
    public void Create_MalformedChdmanRecord_FailsTheWorkerClosed()
    {
        string chdman = Chdman();
        NativeTestBuilds.ChangeRecord(chdman, node => node["binary"]!["path"] = "native/../chdman");

        Should.Throw<ChdmanCandidateException>(() => ChdDeploymentAssets.Create(
            new() { LibchdrDirectory = Libchdr(), ChdmanDirectory = chdman }, requireTool: true)).Rejection.Cause.ShouldBe("ManifestInvalid");
    }

    private string Libchdr() => NativeTestBuilds.WriteLibchdrBuild(Path.Combine(_directory, "libchdr-" + Guid.NewGuid().ToString("N")),
        [1, 2, 3], "linux-arm64", Pin);

    private string Chdman() => NativeTestBuilds.WriteChdmanBuild(Path.Combine(_directory, "chdman-" + Guid.NewGuid().ToString("N")),
        [4, 5, 6], "linux-arm64", ChdmanPin.Tag, ChdmanPin.SourceSha256);

    public void Dispose() => Directory.Delete(_directory, recursive: true);
}
