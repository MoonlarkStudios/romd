using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Romd.Media.Probe;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Storage;

public sealed class MediaProbePathEvidenceTests
{
    [Fact]
    public void Contains_FilesystemRoot_RecognizesDescendant()
    {
        string root = Path.GetPathRoot(Path.GetFullPath(Path.GetTempPath()))!;

        MediaProbePaths.Contains(root, Path.Combine(root, "temp", "staging-v1", "slot-0.data")).ShouldBeTrue();
    }

    [Theory]
    [InlineData("custody")]
    [InlineData("recovery")]
    public async Task Validate_ImmutableInputInsideStaging_RejectsWithoutChanges(string probe)
    {
        using var fixture = await PathFixture.CreateAsync(CancellationToken.None);
        fixture.InputIso = Path.Combine(fixture.ManagedRoot("staging"), "slot-0.data");

        await AssertRejectedAsync(fixture, probe, CancellationToken.None);
    }

    [Theory]
    [InlineData("custody")]
    [InlineData("recovery")]
    public async Task Validate_ImmutableInputAncestorOfData_RejectsWithoutChanges(string probe)
    {
        using var fixture = await PathFixture.CreateAsync(CancellationToken.None);
        fixture.InputIso = fixture.Root;

        await AssertRejectedAsync(fixture, probe, CancellationToken.None);
    }

    [Theory]
    [InlineData("custody")]
    [InlineData("recovery")]
    public async Task Validate_ImmutableInputSymlinkToStaging_RejectsWithoutChanges(string probe)
    {
        using var fixture = await PathFixture.CreateAsync(CancellationToken.None);
        string alias = Path.Combine(fixture.Root, "inputs", "slot-alias.iso");
        File.CreateSymbolicLink(alias, Path.Combine(fixture.ManagedRoot("staging"), "slot-0.data"));
        fixture.InputIso = alias;

        await AssertRejectedAsync(fixture, probe, CancellationToken.None);
    }

    [Theory]
    [InlineData("custody", "config")]
    [InlineData("custody", "report")]
    [InlineData("recovery", "config")]
    [InlineData("recovery", "report")]
    public async Task Validate_ControlPathAliasesFixture_RejectsWithoutChanges(string probe, string control)
    {
        using var fixture = await PathFixture.CreateAsync(CancellationToken.None);
        if (control == "config")
            fixture.InputIso = fixture.ConfigPath;
        else
            fixture.ReportPath = fixture.InputIso;

        await AssertRejectedAsync(fixture, probe, CancellationToken.None);
    }

    public static IEnumerable<object[]> ManagedControlCases()
    {
        foreach (string probe in new[] { "custody", "recovery" })
            foreach (string control in new[] { "config", "report" })
                foreach (string managed in new[] { "staging", "runs", "cas" })
                    yield return [probe, control, managed];
    }

    [Theory]
    [MemberData(nameof(ManagedControlCases))]
    public async Task Validate_ControlPathInsideManagedStorage_RejectsWithoutChanges(string probe, string control, string managed)
    {
        using var fixture = await PathFixture.CreateAsync(CancellationToken.None);
        string path = Path.Combine(fixture.ManagedRoot(managed), "qualification-" + control + ".json");
        if (control == "config")
            fixture.ConfigPath = path;
        else
            fixture.ReportPath = path;

        await AssertRejectedAsync(fixture, probe, CancellationToken.None);
    }

    [Theory]
    [InlineData("state", "staging")]
    [InlineData("state", "runs")]
    [InlineData("state", "cas")]
    [InlineData("ready", "staging")]
    [InlineData("ready", "runs")]
    [InlineData("ready", "cas")]
    public async Task RecoveryValidate_DurableEvidenceInsideManagedStorage_RejectsWithoutChanges(string evidence, string managed)
    {
        using var fixture = await PathFixture.CreateAsync(CancellationToken.None);
        string path = Path.Combine(fixture.ManagedRoot(managed), "qualification-" + evidence + ".json");
        if (evidence == "state")
            fixture.StatePath = path;
        else
            fixture.ReadyPath = path;

        await AssertRejectedAsync(fixture, "recovery", CancellationToken.None);
    }

    [Theory]
    [InlineData("state-data")]
    [InlineData("ready-data")]
    [InlineData("state-ready")]
    public async Task RecoveryValidate_DataAndDurableEvidenceAliases_RejectsWithoutChanges(string alias)
    {
        using var fixture = await PathFixture.CreateAsync(CancellationToken.None);
        if (alias == "state-data")
            fixture.StatePath = fixture.DataDirectory;
        else if (alias == "ready-data")
            fixture.ReadyPath = fixture.DataDirectory;
        else
            fixture.ReadyPath = fixture.StatePath;

        await AssertRejectedAsync(fixture, "recovery", CancellationToken.None);
    }

    [Fact]
    public async Task CustodyValidate_ExistingQualificationBackup_RejectsWithoutChanges()
    {
        using var fixture = await PathFixture.CreateAsync(CancellationToken.None);
        fixture.CustodyOperation = "same-namespace";
        string backup = Path.Combine(fixture.ManagedRoot("runs"), "processing-0.lease.qualification-original");
        await File.WriteAllBytesAsync(backup, [91, 0, 92, 255], CancellationToken.None);

        await AssertRejectedAsync(fixture, "custody", CancellationToken.None);
    }

    [Theory]
    [InlineData("custody")]
    [InlineData("recovery")]
    public async Task Validate_IsolatedInputsControlsAndDurableEvidence_AcceptsWithoutChanges(string probe)
    {
        using var fixture = await PathFixture.CreateAsync(CancellationToken.None);
        Action validate = await fixture.PrepareAsync(probe, CancellationToken.None);

        validate();

        await fixture.AssertPreservedAsync(CancellationToken.None);
    }

    private static async Task AssertRejectedAsync(PathFixture fixture, string probe, CancellationToken ct)
    {
        Action validate = await fixture.PrepareAsync(probe, ct);

        Exception? failure = Record.Exception(validate);

        await fixture.AssertPreservedAsync(ct);
        failure.ShouldNotBeNull("unsafe qualification paths must be rejected before report creation, pool initialization, or lease mutation");
        (failure is TargetInvocationException).ShouldBeFalse("the actual validation failure must be exposed");
    }

    private sealed class PathFixture : IDisposable
    {
        private readonly string _rvzPath, _datPath;
        private readonly Dictionary<string, byte[]> _originalBytes = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string?> _originalLinks = new(StringComparer.Ordinal);
        private string[] _originalEntries = [];
        public string Root { get; }
        public string DataDirectory { get; }
        public string InputIso { get; set; }
        public string ConfigPath { get; set; }
        public string ReportPath { get; set; }
        public string StatePath { get; set; }
        public string ReadyPath { get; set; }
        public string CustodyOperation { get; set; } = "start";

        private PathFixture(string root)
        {
            Root = root;
            DataDirectory = Path.Combine(root, "data");
            InputIso = Path.Combine(root, "inputs", "disc.iso");
            _rvzPath = Path.Combine(root, "inputs", "disc.rvz");
            _datPath = Path.Combine(root, "inputs", "disc.dat");
            ConfigPath = Path.Combine(root, "control", "configuration.json");
            ReportPath = Path.Combine(root, "reports", "new-report.json");
            StatePath = Path.Combine(DataDirectory, "evidence", "state.json");
            ReadyPath = Path.Combine(DataDirectory, "evidence", "ready.json");
        }

        public string ManagedRoot(string managed) => managed switch
        {
            "staging" => Path.Combine(DataDirectory, "temp", "staging-v1"),
            "runs" => Path.Combine(DataDirectory, "temp", "media-runs-v1"),
            "cas" => Path.Combine(DataDirectory, "cas"),
            _ => throw new ArgumentOutOfRangeException(nameof(managed))
        };

        public static async Task<PathFixture> CreateAsync(CancellationToken ct)
        {
            // macOS's default temp path runs through /var, which is itself a symbolic link.
            string temporaryRoot = OperatingSystem.IsMacOS() ? "/private/tmp" : Path.GetTempPath();
            string root = Path.Combine(temporaryRoot, "romd-probe-path-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var fixture = new PathFixture(root);
            try
            {
                foreach (string directory in new[] { "inputs", "control", "reports" })
                    Directory.CreateDirectory(Path.Combine(root, directory));
                Directory.CreateDirectory(Path.GetDirectoryName(fixture.StatePath)!);
                foreach (string managed in new[] { "staging", "runs", "cas" })
                    Directory.CreateDirectory(fixture.ManagedRoot(managed));
                string[] files = [fixture.InputIso, fixture._rvzPath, fixture._datPath,
                    Path.Combine(fixture.ManagedRoot("staging"), "slot-0.data"),
                    Path.Combine(fixture.ManagedRoot("staging"), "slot-0.lease"),
                    Path.Combine(fixture.ManagedRoot("staging"), "reservations.json"),
                    Path.Combine(fixture.ManagedRoot("runs"), "processing-0.lease"),
                    Path.Combine(fixture.ManagedRoot("runs"), "run-0.json"),
                    Path.Combine(fixture.ManagedRoot("runs"), "run-0.json.identity"),
                    Path.Combine(fixture.ManagedRoot("cas"), "retained-artifact")];
                for (int index = 0; index < files.Length; index++)
                    await File.WriteAllBytesAsync(files[index], [0, (byte)index, 127, 255, 0], ct);
                return fixture;
            }
            catch { fixture.Dispose(); throw; }
        }

        public async Task<Action> PrepareAsync(string probe, CancellationToken ct)
        {
            Type type = probe switch
            {
                "custody" => typeof(CustodyChecks),
                "recovery" => typeof(ConvergenceRecoveryChecks),
                _ => throw new ArgumentOutOfRangeException(nameof(probe))
            };
            var payload = new Dictionary<string, object?>
            {
                ["Operation"] = probe == "custody" ? CustodyOperation : "seed",
                ["DataDirectory"] = DataDirectory,
                ["ProductionWorker"] = "/app/media-worker/Romd.Media.Worker.dll"
            };
            if (probe == "custody")
            {
                payload["InputIso"] = InputIso;
                payload["JobId"] = Guid.NewGuid();
                payload["DiscKind"] = "gamecube";
            }
            else
            {
                payload["Checkpoint"] = "encoding";
                payload["DatabaseName"] = "romd_gate9_paths_" + Guid.NewGuid().ToString("N");
                payload["StatePath"] = StatePath;
                payload["ReadyPath"] = ReadyPath;
                payload["Fixture"] = new
                {
                    Name = "synthetic-path-boundary-fixture",
                    UploadedRvz = _rvzPath,
                    NormalizedRvz = Path.Combine(Root, "inputs", "normalized.rvz"),
                    Iso = InputIso,
                    Dat = _datPath,
                    DatId = "synthetic",
                    NintendoKind = "gamecube"
                };
            }
            byte[] json = JsonSerializer.SerializeToUtf8Bytes(payload);
            await File.WriteAllBytesAsync(ConfigPath, json, ct);
            Type configurationType = type.GetNestedType("Configuration", BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("Probe configuration type was not found.");
            object configuration = JsonSerializer.Deserialize(json, configurationType,
                new JsonSerializerOptions { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow })
                ?? throw new InvalidOperationException("Valid test configuration was not deserialized.");
            MethodInfo method = type.GetMethod("Validate", BindingFlags.Static | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("Probe validator was not found.");
            object[] parameters = method.GetParameters().Length switch
            {
                1 when probe == "custody" => [configuration],
                3 => [configuration, ConfigPath, ReportPath],
                _ => throw new InvalidOperationException("Unsupported probe validation signature.")
            };

            _originalEntries = Entries();
            foreach (string file in Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories))
            {
                _originalBytes[file] = await File.ReadAllBytesAsync(file, ct);
                _originalLinks[file] = new FileInfo(file).LinkTarget;
            }
            return () =>
            {
                try
                { method.Invoke(null, parameters); }
                catch (TargetInvocationException error) when (error.InnerException is { } inner)
                {
                    ExceptionDispatchInfo.Capture(inner).Throw();
                    throw;
                }
            };
        }

        public async Task AssertPreservedAsync(CancellationToken ct)
        {
            Entries().ShouldBe(_originalEntries, "validation must not create or remove reports, state, ready evidence, fixtures, or managed pool entries");
            foreach (var original in _originalBytes)
            {
                (await File.ReadAllBytesAsync(original.Key, ct)).ShouldBe(original.Value,
                    "immutable fixture, configuration, lease, accounting, custody, backup, and CAS bytes must remain exact: " + original.Key);
                new FileInfo(original.Key).LinkTarget.ShouldBe(_originalLinks[original.Key], "validation must not replace a symbolic-link input");
            }
        }

        private string[] Entries() => Directory.EnumerateFileSystemEntries(Root, "*", SearchOption.AllDirectories)
            .Order(StringComparer.Ordinal).ToArray();

        public void Dispose() => Directory.Delete(Root, true);
    }
}
