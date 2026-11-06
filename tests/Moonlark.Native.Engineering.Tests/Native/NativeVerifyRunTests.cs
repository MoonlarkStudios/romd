using System.Globalization;
using System.Runtime.Versioning;
using System.Text.Json.Nodes;
using Moonlark.Native.Engineering.Core;
using Moonlark.Native.Engineering.Native;
using Moonlark.Native.Engineering.Tests.TestSupport;
using Xunit;

namespace Moonlark.Native.Engineering.Tests.Native;

/// <summary>
/// The whole <c>native verify</c> path for a synthetic manifest: source identity and timestamp, then fake-tool inspection of the
/// manifest's binary, whose actual symbols, dependencies and platform must equal what the manifest recorded.
/// </summary>
public sealed class NativeVerifyRunTests
{
    /// <summary>A manifest that agrees with its source and with what its binary reports verifies, after inspecting that binary.</summary>
    [Fact]
    public void ConsistentManifestSourceAndBinaryVerify()
    {
        if (OperatingSystem.IsWindows())
            return;
        using var verify = new VerifyCase();
        Assert.Equal(verify.Binary, verify.Run().Value);
        Assert.Equal<string>([verify.Binary], verify.Reads);
        Assert.Contains("$ readelf -h " + verify.Binary, verify.Log.ToString(), StringComparison.Ordinal);
    }

    /// <summary>The production overload inspects with this machine's RID and really loads the binary, so a synthetic one fails there.</summary>
    [Fact]
    public void DefaultHostIsThisMachineAndItsInProcessLoader()
    {
        if (OperatingSystem.IsWindows())
            return;
        using var verify = new VerifyCase();
        string expected = NativeRids.Host().Value == VerifyCase.Rid ? "Native binary could not be loaded" : "requires its native host";
        Assert.Contains(expected, verify.RunOnThisHost().Failure.Message, StringComparison.Ordinal);
    }

    /// <summary>The recipe's SOURCE_DATE_EPOCH must be the verified source's commit timestamp; the binary is never inspected otherwise.</summary>
    [Fact]
    public void SourceTimestampMustEqualTheRecipeEpoch()
    {
        if (OperatingSystem.IsWindows())
            return;
        using var verify = new VerifyCase(epochOffset: 1);
        Assert.Equal("Source timestamp mismatch", verify.Run().Failure.Message);
        Assert.Empty(verify.Reads);
    }

    /// <summary>Allowlist-valid recorded symbols, dependencies or platform that differ from the actual binary fail.</summary>
    [Theory]
    [InlineData("symbols")]
    [InlineData("dependencies")]
    [InlineData("platform")]
    public void ActualBinaryFactsMustEqualTheManifest(string field)
    {
        if (OperatingSystem.IsWindows())
            return;
        using var verify = new VerifyCase();
        verify.Manifest[field] = field switch
        {
            // The same allowlisted set in another order; the actual binary's exports are recorded sorted.
            "symbols" => JsonFields.Array(verify.Allowlist.OrderDescending(StringComparer.Ordinal)),
            "dependencies" => JsonFields.Array(["libc.so.6"]),
            _ => new JsonObject { ["architecture"] = "Advanced Micro Devices X86-64", ["maximumRequiredGlibc"] = "2.31", ["glibcBaseline"] = "2.31" },
        };
        Assert.Equal($"Actual binary {field} differs from manifest", verify.Run().Failure.Message);
        Assert.Equal<string>([verify.Binary], verify.Reads);
    }
}

/// <summary>
/// A linux-x64 manifest whose pin, recipe epoch and build-info agree with a committed synthetic source, plus canned readelf/nm
/// replies for its binary. The host RID and build-info reader are substituted; git runs for real.
/// </summary>
internal sealed class VerifyCase : IDisposable
{
    internal const string Rid = "linux-x64";

    private readonly NativeRoot _root = new();
    private readonly List<string> _reads = [];
    private readonly Dictionary<string, (string[] Command, CannedReply Reply)> _replies;

    internal VerifyCase(long epochOffset = 0)
    {
        Allowlist = _root.Allowlist;
        (Source, LibchdrPin pin) = DeclaringSource(_root.Path, Allowlist);
        _root.WriteAuthorityFor(pin);
        LibchdrAuthority authority = Authorities.ReadLibchdr(_root.Path).Value;
        long epoch = long.Parse(TestRepository.Git(Source, "show", "-s", "--format=%ct", "HEAD"), CultureInfo.InvariantCulture);
        Binary = Path.Combine(_root.Path, NativeOutput.NativeDirectory, NativeRids.LibraryFileName(Rid));
        Directory.CreateDirectory(Path.GetDirectoryName(Binary)!);
        File.WriteAllBytes(Binary, "synthetic binary; never loaded"u8.ToArray());
        JsonObject recipe = BuildRecipe.Create(authority, Rid, NativeRoot.Configuration(Rid), NativeRoot.Toolchain(), epoch + epochOffset,
            BuildRecipe.InputDigests(_root.Path).Value);
        var recorded = new Inspection([.. Allowlist.Order(StringComparer.Ordinal)], ["libc.so.6", "libm.so.6"], CannedTools.Platform(Rid));
        Manifest = NativeManifest.Create(authority, Rid, Binary, recipe, recorded, new JsonObject { ["sourceRoot"] = _root.Path });
        // Reported unsorted, as tools do; the manifest records the validated, sorted facts.
        _replies = CannedTools.For(Rid, Binary, [.. Allowlist.Reverse()], CannedTools.Dependencies(Rid));
    }

    internal IReadOnlyList<string> Allowlist { get; }

    internal string Source { get; }

    internal string Binary { get; }

    /// <summary>The manifest as built; tests change fields before <see cref="Run"/> writes it.</summary>
    internal JsonObject Manifest { get; }

    internal IReadOnlyList<string> Reads => _reads;

    internal StringWriter Log { get; } = new();

    [UnsupportedOSPlatform("windows")]
    internal Result<string> Run()
    {
        (string manifest, IReadOnlyDictionary<string, string> environment) = Prepare();
        JsonObject embedded = (JsonObject)Manifest["buildInfo"]!.DeepClone();
        var host = new InspectionHost(() => Rid, binary =>
        {
            _reads.Add(binary);
            return embedded;
        });
        return NativeVerify.Run(_root.Path, manifest, Source, environment, Log, host);
    }

    /// <summary>Verifies through the production overload: this machine's RID and the real in-process loader.</summary>
    [UnsupportedOSPlatform("windows")]
    internal Result<string> RunOnThisHost()
    {
        (string manifest, IReadOnlyDictionary<string, string> environment) = Prepare();
        return NativeVerify.Run(_root.Path, manifest, Source, environment, Log);
    }

    public void Dispose()
    {
        Log.Dispose();
        _root.Dispose();
    }

    /// <summary>Writes the manifest and the fake tools; the fakes come first on PATH and git still resolves from the clean PATH.</summary>
    [UnsupportedOSPlatform("windows")]
    private (string Manifest, IReadOnlyDictionary<string, string> Environment) Prepare()
    {
        string manifest = Path.Combine(_root.Path, NativeOutput.ManifestName);
        File.WriteAllBytes(manifest, JsonFields.Serialize(Manifest, sortKeys: true));
        string bin = FakeTools.Install(Path.Combine(_root.Path, "fake-tools"), _replies.Values);
        var environment = new Dictionary<string, string>(TestRepository.CleanEnvironment, StringComparer.Ordinal)
        {
            ["PATH"] = bin + Path.PathSeparator + TestRepository.CleanEnvironment["PATH"],
        };
        return (manifest, environment);
    }

    /// <summary>A committed synthetic upstream whose public header declares exactly the allowlisted functions, and its matching pin.</summary>
    private static (string Source, LibchdrPin Pin) DeclaringSource(string parent, IReadOnlyList<string> allowlist)
    {
        (string source, LibchdrPin pin) = SyntheticSource.Create(parent);
        File.WriteAllText(Path.Combine(source, Authorities.HeaderPaths[0]),
            string.Concat(allowlist.Where(name => name != ExportInventory.BuildInfoExport).Select(name => "CHD_EXPORT int " + name + "(void);\n")));
        TestRepository.Git(source, "-c", "user.name=Synthetic Test", "-c", "user.email=synthetic@example.invalid", "-c", "commit.gpgsign=false",
            "commit", "-qam", "declare synthetic exports");
        return (source, pin with
        {
            Commit = TestRepository.Git(source, "rev-parse", "HEAD"),
            UpstreamVersion = TestRepository.Git(source, "describe", "--always", "--tags", "--long"),
            Headers = Authorities.HeaderPaths.ToDictionary(name => name, name => Digest.Sha256File(Path.Combine(source, name)), StringComparer.Ordinal),
        });
    }
}
