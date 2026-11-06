using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Nodes;
using Moonlark.Libchdr.Internal;
using Xunit;

namespace Moonlark.Libchdr.Tests;

/// <summary>Qualifies libchdr Library Tests.</summary>
public sealed class LibchdrLibraryTests
{
    /// <summary>Qualifies actual Build Identity And Explicit Path Are Stable And Immutable.</summary>
    [Fact]
    public void ActualBuildIdentityAndExplicitPathAreStableAndImmutable()
    {
        string root = NativeTestEnvironment.Root;
        (string rid, string name) = NativePlatform.Current();
        string binary = Path.Combine(root, "artifacts", "native", "libchdr", rid, "native", name);
        LibchdrBuildInfo identity = LibchdrLibrary.BuildInfo;
        LibchdrLibrary.Load(binary);
        Assert.Same(identity, LibchdrLibrary.BuildInfo);
        Assert.Equal(NativeBuildContract.FamilyVersion, identity.FamilyVersion);
        Assert.Equal(NativeBuildContract.UpstreamCommit, identity.UpstreamCommit);
        Assert.True(identity.HasRawSectors);
        Assert.True(identity.HasSubcode);
        Assert.True(identity.VerifiesBlockCrc);
        Assert.Throws<InvalidOperationException>(() => LibchdrLibrary.Load(Path.Combine(root, "other", name)));
        Assert.Throws<ArgumentException>(() => LibchdrLibrary.Load(name));
    }

    /// <summary>Qualifies build Info Rejects Incompatible Or Missing Fields.</summary>
    [Theory]
    [InlineData("managedVersion")]
    [InlineData("nativeVersion")]
    [InlineData("upstreamVersion")]
    [InlineData("upstreamCommit")]
    [InlineData("schemaVersion")]
    [InlineData("abiVersion")]
    [InlineData("buildId")]
    [InlineData("headers")]
    [InlineData("features")]
    public void BuildInfoRejectsIncompatibleOrMissingFields(string field)
    {
        JsonObject original = BuildInfo();
        JsonObject missing = (JsonObject)original.DeepClone();
        missing.Remove(field);
        Assert.Throws<BadImageFormatException>(() => Parse(missing));
        original[field] = field is "schemaVersion" or "abiVersion" ? JsonValue.Create(7) : JsonValue.Create("wrong");
        Assert.Throws<BadImageFormatException>(() => Parse(original));
    }

    /// <summary>Qualifies build Info Rejects Duplicate Properties And Missing Or Duplicate Features.</summary>
    [Fact]
    public void BuildInfoRejectsDuplicatePropertiesAndMissingOrDuplicateFeatures()
    {
        JsonObject original = BuildInfo();
        string json = original.ToJsonString();
        Assert.Throws<BadImageFormatException>(() => NativeBuildInfoReader.Parse(Encoding.UTF8.GetBytes(json[..^1] + ",\"abiVersion\":1}")));
        original["features"] = new JsonArray("raw-sectors", "subcode", "subcode", "block-crc");
        Assert.Throws<BadImageFormatException>(() => Parse(original));
        original["features"] = new JsonArray("raw-sectors", "subcode");
        Assert.Throws<BadImageFormatException>(() => Parse(original));
        original = BuildInfo();
        original["headers"]!["include/libchdr/coretypes.h"] = "00";
        Assert.Throws<BadImageFormatException>(() => Parse(original));
        Assert.Throws<BadImageFormatException>(() => NativeBuildInfoReader.Parse("[]"u8));
    }

    /// <summary>Qualifies resolves Only Supported Platform Contracts.</summary>
    [Theory]
    [InlineData("linux", Architecture.X64, "linux-x64", "libmoonlark_chdr.so")]
    [InlineData("linux", Architecture.Arm64, "linux-arm64", "libmoonlark_chdr.so")]
    [InlineData("macos", Architecture.Arm64, "osx-arm64", "libmoonlark_chdr.dylib")]
    [InlineData("windows", Architecture.X64, "win-x64", "moonlark_chdr.dll")]
    public void ResolvesOnlySupportedPlatformContracts(string system, Architecture architecture, string rid, string filename) =>
        Assert.Equal((rid, filename), NativePlatform.Resolve(system, architecture, rid));

    /// <summary>Qualifies unsupported Platforms Fail With No Fallback.</summary>
    [Theory]
    [InlineData("linux", Architecture.X64, "linux-musl-x64")]
    [InlineData("linux", Architecture.Arm, "linux-arm")]
    [InlineData("macos", Architecture.X64, "osx-x64")]
    [InlineData("windows", Architecture.Arm64, "win-arm64")]
    public void UnsupportedPlatformsFailWithNoFallback(string system, Architecture architecture, string rid) =>
        Assert.Throws<PlatformNotSupportedException>(() => NativePlatform.Resolve(system, architecture, rid));

    private static JsonObject BuildInfo()
    {
        (string rid, _) = NativePlatform.Current();
        JsonNode receipt = JsonNode.Parse(File.ReadAllText(Path.Combine(NativeTestEnvironment.Root, "artifacts", "native", "libchdr", rid, "build-manifest.json")))!;
        return (JsonObject)receipt["buildInfo"]!.DeepClone();
    }
    private static LibchdrBuildInfo Parse(JsonObject value) => NativeBuildInfoReader.Parse(Encoding.UTF8.GetBytes(value.ToJsonString()));
}
