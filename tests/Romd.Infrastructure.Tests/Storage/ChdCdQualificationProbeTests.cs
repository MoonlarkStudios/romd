using Romd.Application.Common.Content;
using Romd.Infrastructure.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Romd.PostgreSql.TestSupport;
using Romd.Storage;
using Romd.Domain.Source.Rom;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Romd.Media.Probe;
using Romd.Media.TestSupport;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Storage;

public sealed class ChdCdQualificationProbeTests
{
    private static ChdCdQualificationInputs.Input Input => new("fixture", "/fixtures", "original.cue", [], "/oracle.dat", new string('a', 64), "Game", 128 << 20);
    private static ChdCdQualificationInputs.Configuration Config => new("Independent DAT provenance", "real", "/native", "/tool", "/fresh-data", [Input]);
    private static string Rom(string name = "track.bin") => $"<rom name='{name}' size='2352' crc='00000000' md5='{new string('a', 32)}' sha1='{new string('b', 40)}'/>";
    private static IReadOnlyDictionary<string, ChdCdQualificationInputs.DatMember> Read(string xml) =>
        ChdCdQualificationInputs.ReadDat(new MemoryStream(Encoding.UTF8.GetBytes(xml)), "Game");

    [Theory]
    [InlineData("fixture")]
    [InlineData("native")]
    [InlineData("tool")]
    [InlineData("data")]
    [InlineData("temp")]
    [InlineData("cas")]
    [InlineData("executable")]
    public async Task VfsLoad_FreshReportInsideProtectedRoot_RefusesBeforeNativeWorkOrAnyMutation(string location)
    {
        string root = Path.Combine(Path.GetTempPath(), "romd-vfs-protected-output-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            foreach (string name in new[] { "fixture", "native", "tool" })
            {
                Directory.CreateDirectory(Path.Combine(root, name));
                File.WriteAllBytes(Path.Combine(root, name, "sentinel"), [1, 2, 3, 255]);
            }
            string dat = Path.Combine(root, "oracle.dat");
            File.WriteAllBytes(dat, [4, 5, 6]);
            var config = Config with
            {
                NativeDirectory = Path.Combine(root, "native"),
                ToolDirectory = Path.Combine(root, "tool"),
                DataDirectory = Path.Combine(root, "data"),
                Inputs = [Input with { FixtureDirectory = Path.Combine(root, "fixture"), DatPath = dat }]
            };
            string configuration = Path.Combine(root, "configuration.json");
            File.WriteAllText(configuration, JsonSerializer.Serialize(config));
            var before = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).ToDictionary(Path.GetFullPath, File.ReadAllBytes);
            string target = location switch
            {
                "executable" => Path.Combine(AppContext.BaseDirectory, "uncreated-load-" + Guid.NewGuid().ToString("N") + ".json"),
                "temp" => Path.Combine(config.DataDirectory, "temp", "load.json"),
                "cas" => Path.Combine(config.DataDirectory, "read-load-cas", "load.json"),
                _ => Path.Combine(root, location, "load.json")
            };
            Func<Task> runProbe = () => ChdCdVfsReadChecks.RunAsync(
                ["--chd-cd-vfs-load-probe", configuration, Path.Combine(root, "qualification.json"), target, "1800"], CancellationToken.None);
            if (OperatingSystem.IsLinux())
            {
                var error = await Should.ThrowAsync<InvalidOperationException>(runProbe);
                error.Message.ShouldBe("FAILED: qualification outputs outside inputs and executable directories");
            }
            else
            {
                var error = await Should.ThrowAsync<PlatformNotSupportedException>(runProbe);
                error.Message.ShouldBe("Supervised CD encoding qualification requires native Linux.");
            }
            File.Exists(target).ShouldBeFalse();
            File.Exists(Path.Combine(root, "qualification.json")).ShouldBeFalse();
            Directory.Exists(config.DataDirectory).ShouldBeFalse();
            Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Order().ShouldBe(before.Keys.Order());
            foreach (var file in before)
                File.ReadAllBytes(file.Key).ShouldBe(file.Value);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task VfsLoad_OverlappingFreshReports_RefusesBeforeCreatingEitherReportOrManagedData()
    {
        string root = Path.Combine(Path.GetTempPath(), "romd-vfs-overlapping-output-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var config = Config with
            {
                NativeDirectory = Path.Combine(root, "native"),
                ToolDirectory = Path.Combine(root, "tool"),
                DataDirectory = Path.Combine(root, "data"),
                Inputs = [Input with { FixtureDirectory = Path.Combine(root, "fixture"), DatPath = Path.Combine(root, "oracle.dat") }]
            };
            string configuration = Path.Combine(root, "configuration.json");
            File.WriteAllText(configuration, JsonSerializer.Serialize(config));
            string report = Path.Combine(root, "qualification.json");
            Func<Task> runProbe = () => ChdCdVfsReadChecks.RunAsync(
                ["--chd-cd-vfs-load-probe", configuration, report, Path.Combine(report, "load.json"), "1800"], CancellationToken.None);
            if (OperatingSystem.IsLinux())
            {
                var error = await Should.ThrowAsync<InvalidOperationException>(runProbe);
                error.Message.ShouldBe("FAILED: qualification report destinations cannot overlap each other");
            }
            else
            {
                var error = await Should.ThrowAsync<PlatformNotSupportedException>(runProbe);
                error.Message.ShouldBe("Supervised CD encoding qualification requires native Linux.");
            }
            Directory.EnumerateFileSystemEntries(root).ShouldBe([configuration]);
        }
        finally { Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData("1")]
    [InlineData("1799")]
    [InlineData("86401")]
    [InlineData("invalid")]
    public async Task VfsLoad_InvalidDuration_RefusesBeforeAnyInputOrReportIo(string seconds)
    {
        await Should.ThrowAsync<ArgumentException>(() => ChdCdVfsReadChecks.RunAsync(
            ["--chd-cd-vfs-load-probe", "/unopened/config.json", "/unopened/qualification.json", "/unopened/load.json", seconds], CancellationToken.None));
    }

    [Fact]
    public async Task VfsLoad_ExistingReport_PreservesBytesBeforeReadingTheQualificationConfiguration()
    {
        string directory = Path.Combine(Path.GetTempPath(), "romd-vfs-load-report-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string report = Path.Combine(directory, "existing.json");
            byte[] original = [1, 2, 3, 4, 255];
            await File.WriteAllBytesAsync(report, original);
            await Should.ThrowAsync<InvalidOperationException>(() => ChdCdVfsReadChecks.RunAsync(
                ["--chd-cd-vfs-load-probe", Path.Combine(directory, "unopened.json"), Path.Combine(directory, "qualification.json"), report, "1800"], CancellationToken.None));
            (await File.ReadAllBytesAsync(report)).ShouldBe(original);
            File.Exists(Path.Combine(directory, "qualification.json")).ShouldBeFalse();
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task RunAsync_GenuineNativeEncoding_RecordsIndependentWholeFilesAndExplicitSyntheticScope()
    {
        string root = Path.Combine(Path.GetTempPath(), "romd-cd-qualification-" + Guid.NewGuid().ToString("N"));
        string fixtures = Path.Combine(root, "fixtures");
        Directory.CreateDirectory(fixtures);
        try
        {
            // Enough genuine work for the unchanged executed-image observer; no tiny-exit exemption.
            byte[] template = File.ReadAllBytes(NativeTestBuilds.LibchdrFixture("cd-source.bin"))[..(16 * 2352)];
            string bin = Path.Combine(fixtures, "track.bin");
            await using (var output = File.Create(bin))
                for (int remaining = 32769 * 2352; remaining > 0;)
                { int count = Math.Min(remaining, template.Length); await output.WriteAsync(template.AsMemory(0, count)); remaining -= count; }
            var hashes = await Digests.FileAsync(bin);
            File.WriteAllText(Path.Combine(fixtures, "original.cue"), "REM exact original descriptor\r\nFILE \"track.bin\" BINARY\r\n TRACK 01 MODE1/2352\r\n INDEX 01 00:00:00\r\n");
            File.WriteAllBytes(Path.Combine(fixtures, "original.sbi"), [0x53, 0x42, 0x49, 0, 0xfe]);
            string dat = Path.Combine(root, "oracle.dat");
            var cueHashes = await Digests.FileAsync(Path.Combine(fixtures, "original.cue"));
            File.WriteAllText(dat, $"<datafile><game name='Game'><rom name='original.cue' size='{cueHashes.Size}' crc='{cueHashes.Crc}' md5='{cueHashes.Md5}' sha1='{cueHashes.Sha1}'/><rom name='track.bin' size='{hashes.Size}' crc='{hashes.Crc}' md5='{hashes.Md5}' sha1='{hashes.Sha1}'/></game></datafile>");
            var config = Config with
            {
                FixtureKind = "synthetic",
                NativeDirectory = NativeTestBuilds.LibchdrDirectory,
                ToolDirectory = NativeTestBuilds.ChdmanDirectory,
                DataDirectory = Path.Combine(root, "data"),
                Inputs = [Input with
                { FixtureDirectory = fixtures, Sidecars = ["original.sbi"], DatPath = dat, DatSha256 = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(dat))) }]
            };
            string configuration = Path.Combine(root, "configuration.json"), report = Path.Combine(root, "report.json");
            File.WriteAllText(configuration, JsonSerializer.Serialize(config));
            if (!OperatingSystem.IsLinux())
            {
                await Should.ThrowAsync<PlatformNotSupportedException>(() => ChdCdQualificationChecks.RunAsync(
                    ["--chd-cd-qualification-probe", configuration, report], ChdCdQualificationChecks.Retention.MeasurementsOnly, [], CancellationToken.None));
                File.Exists(report).ShouldBeFalse();
                Directory.Exists(config.DataDirectory).ShouldBeFalse();
                return;
            }
            var qualified = await ChdCdQualificationChecks.RunAsync(["--chd-cd-qualification-probe", configuration, report], ChdCdQualificationChecks.Retention.ArchiveForReadLoad, [], CancellationToken.None);
            var retainedArchive = qualified.Archives.ShouldHaveSingleItem();
            File.Exists(retainedArchive.ArtifactPath).ShouldBeTrue("read-load retention survives stopped worker and temporary artifact disposal");
            var retainedBytes = await Digests.FileAsync(retainedArchive.ArtifactPath);
            retainedBytes.Size.ShouldBe(retainedArchive.Measured.Measurement.ArtifactSize);
            retainedBytes.Sha256.ShouldBe(retainedArchive.Measured.Measurement.ArtifactSha256.ToString());
            await using (var readDatabase = PostgreSqlTestDatabase.Create())
            await using (var services = new ServiceCollection().AddContentAddressableStorage(options =>
            {
                options.RootPath = Path.Combine(root, "read-cas");
                options.WritePolicy = ContentWritePolicy.Raw;
                options.MaxContentSize = 1L << 30;
            }).BuildServiceProvider())
            await using (var db = readDatabase.CreateContext())
            {
                IReadOnlyList<(int RomId, string OraclePath)> published;
                await using (var transaction = await db.Database.BeginTransactionAsync())
                {
                    published = await ChdCdVfsReadChecks.PublishReadLibraryAsync(db,
                        services.GetRequiredService<IContentAddressableStore>(), qualified.Archives, CancellationToken.None);
                    await transaction.CommitAsync();
                }
                var member = published.ShouldHaveSingleItem();
                member.OraclePath.ShouldBe(bin);
                var content = (await MediaTestServices.CreateContentCatalog(db).FindByRomIdAsync(member.RomId)).ShouldNotBeNull();
                content.Encoding.ShouldBe(RomRepresentationEncoding.Chd);
                content.Artifacts.Count.ShouldBe(1);
                content.Preferred.Facts.Outputs.ShouldHaveSingleItem().RomId.ShouldBe(member.RomId);
                content.Preferred.Facts.Descriptors.Count.ShouldBe(2, "CUE and sidecar are retained descriptors, not logical outputs");
                content.Preferred.Facts.Verifications.Single().Id.ShouldBe(retainedArchive.Measured.Measurement.Id);
                content.Preferred.Facts.Creations.ShouldBeEmpty();
                (await db.RomFiles.CountAsync()).ShouldBe(1);
                (await db.Files.AnyAsync(file => file.Sha256 == content.ContentSha256)).ShouldBeFalse("the BIN has only measured CHD backing");
                var media = RomMediaPolicyComposition.Create();
                var read = ChdCdVfsReadChecks.CreateReadComposition(config.NativeDirectory, media, CancellationToken.None);
                await using var readOwner = read.Decoder;
                read.Decoder.Decoder.CanDecode(RomRepresentationEncoding.Chd).ShouldBeTrue();
                read.Decoder.Options.MaxConcurrent.ShouldBe(4);
                read.Decoder.Options.MaxIdle.ShouldBe(2);
                read.Decoder.Options.CacheBytes.ShouldBe(64L << 20);
                var target = RomDeliveryPlanner.Plan(content, "track.bin", media.DeliveryPolicy,
                    MediaKindResolver.Resolve(media.Kinds, content, media.CdRecipe!.PlatformKeys), read.Admission)
                    .Single(option => option.Format == RomDeliveryFormat.Original).Target.ShouldNotBeNull();
                var readCatalog = new ProbeCatalog(readDatabase.CreateOptions());
                var parallelSnapshots = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => readCatalog.FindByRomIdAsync(member.RomId)));
                parallelSnapshots.ShouldAllBe(snapshot => snapshot != null && snapshot.ContentSha256 == content.ContentSha256 &&
                    snapshot.ArtifactSha256 == retainedArchive.Measured.Measurement.ArtifactSha256);
                var reader = new RomContentReader(readCatalog,
                    services.GetRequiredService<IContentAddressableStore>(), read.Decoder.Decoder, read.Admission);
                var held = new List<Stream>();
                try
                {
                    for (int index = 0; index < 4; index++)
                        held.Add((await reader.OpenSeekableAsync(target)).ShouldNotBeNull());
                    await Should.ThrowAsync<ResourceCapacityException>(() => reader.OpenSeekableAsync(target));
                    (await Digests.StreamAsync(held[0])).ShouldBe(hashes);
                    held[0].Seek(2352, SeekOrigin.Begin);
                    byte[] sample = new byte[2352];
                    await held[0].ReadExactlyAsync(sample);
                    sample.ShouldBe(template[2352..4704]);
                }
                finally { foreach (var stream in held) await stream.DisposeAsync(); }
                await Task.WhenAll(Enumerable.Range(0, 4).Select(async _ =>
                {
                    await using var stream = (await reader.OpenSeekableAsync(target)).ShouldNotBeNull();
                    byte[] bytes = new byte[2352];
                    await stream.ReadExactlyAsync(bytes);
                    bytes.ShouldBe(template[..2352]);
                }));
                await read.Decoder.DisposeAsync();
                var native = read.Decoder.NativeSnapshot.ShouldNotBeNull();
                native.LiveHandles.ShouldBe(0);
                native.ActiveHandles.ShouldBe(0);
                native.IdleHandles.ShouldBe(0);
                native.Flights.ShouldBe(0);
                native.FlightWaiters.ShouldBe(0);
                native.Artifacts.ShouldBe(0);
                native.Speculations.ShouldBe(0);
                native.OpeningHandles.ShouldBe(0);
                native.OpenedHandles.ShouldBe(native.ClosedHandles);
                native.PeakLiveHandles.ShouldBeLessThanOrEqualTo(4);

                var publishedCatalog = await ChdCdReadCatalog.SeedAsync(db, qualified.Archives, CancellationToken.None);
                var endpointMember = publishedCatalog.Members.ShouldHaveSingleItem();
                endpointMember.Identity.Sha256.ShouldBe(content.ContentSha256);
                await using (var host = await ChdCdInteractiveReadHost.StartAsync(readDatabase,
                    services.GetRequiredService<IContentAddressableStore>(), media, config.NativeDirectory,
                    publishedCatalog.Users, CancellationToken.None))
                {
                    await ChdCdInteractiveReadChecks.AssertAuthorityAsync(host, readDatabase, publishedCatalog, CancellationToken.None);
                    using var request = new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Get, endpointMember.Url);
                    request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(2352, 4703);
                    using var response = await host.Clients[2].SendAsync(request);
                    response.StatusCode.ShouldBe(System.Net.HttpStatusCode.PartialContent);
                    (await response.Content.ReadAsByteArrayAsync()).ShouldBe(template[2352..4704]);
                    var interactiveCase = new ChdCdInteractiveReadChecks.Case(endpointMember, 2352, "known-sector",
                        new(endpointMember.Url, endpointMember.Identity.Size, endpointMember.Identity.Sha256, 2352, 2352, template[2352..4704]));
                    var evidence = new InteractiveReadEvidence([interactiveCase]);
                    await ChdCdInteractiveReadChecks.PhaseAsync(host, [interactiveCase], publishedCatalog.Members,
                        "idle", 0, TimeSpan.FromSeconds(1), evidence, () => Task.CompletedTask, CancellationToken.None);
                    using var interactiveReport = JsonDocument.Parse(JsonSerializer.Serialize(evidence.Report()));
                    var phaseResources = interactiveReport.RootElement.GetProperty("Phases")[0].GetProperty("Resources");
                    phaseResources.GetProperty("Samples").GetInt64().ShouldBeGreaterThan(0);
                    phaseResources.GetProperty("FailureCode").ValueKind.ShouldBe(JsonValueKind.Null);
                    phaseResources.GetProperty("SamplingIntervalMilliseconds").GetInt32().ShouldBe(250);
                    await host.DisposeAsync();
                    host.Runtime.NativeSnapshot.ShouldNotBeNull().LiveHandles.ShouldBe(0);
                }
                (await db.Files.AnyAsync(file => file.Sha256 == content.ContentSha256)).ShouldBeFalse(
                    "authenticated loopback still decodes the BIN from CHD, while exact descriptor identities have their own original graphs");

            }

            using var document = JsonDocument.Parse(File.ReadAllBytes(report));
            var result = document.RootElement;
            result.GetProperty("Status").GetString().ShouldBe("passed");
            result.GetProperty("FixtureKind").GetString().ShouldBe("synthetic");
            result.GetProperty("ProbeAssemblySha256").GetString()!.Length.ShouldBe(64);
            var row = result.GetProperty("Rows")[0];
            row.GetProperty("Coverage").GetProperty("DataOnly").GetBoolean().ShouldBeTrue();
            row.GetProperty("Originals").GetArrayLength().ShouldBe(3);
            row.GetProperty("DatMatchedMembers").GetArrayLength().ShouldBe(2);
            row.GetProperty("DatMatchedMembers").EnumerateArray().Select(member => member.GetString()).ShouldContain("original.cue");
            row.GetProperty("Encoding").GetProperty("ProcessTreeStopped").GetBoolean().ShouldBeTrue();
            row.GetProperty("Verification").GetProperty("ProcessTreeStopped").GetBoolean().ShouldBeTrue();
            row.GetProperty("Encoding").GetProperty("Execution").GetProperty("ProcessId").GetInt32()
                .ShouldNotBe(row.GetProperty("Verification").GetProperty("Execution").GetProperty("ProcessId").GetInt32());
            row.GetProperty("OriginalsUnchanged").GetBoolean().ShouldBeTrue();
            (await Digests.FileAsync(bin)).ShouldBe(hashes);
            // Catalog descriptors can name different BIN files. Preserve the original descriptor,
            // explicitly bind the BIN identity, and never claim that the catalog CUE was matched.
            string renamedDat = Path.Combine(root, "renamed.dat");
            byte[] catalogCue = Encoding.UTF8.GetBytes(File.ReadAllText(Path.Combine(fixtures, "original.cue"))
                .Replace("track.bin", "catalog.bin", StringComparison.Ordinal));
            var catalogHashes = await Digests.StreamAsync(new MemoryStream(catalogCue));
            File.WriteAllText(renamedDat, $"<datafile><game name='Game'><rom name='catalog.cue' size='{catalogHashes.Size}' crc='{catalogHashes.Crc}' md5='{catalogHashes.Md5}' sha1='{catalogHashes.Sha1}'/><rom name='catalog.bin' size='{hashes.Size}' crc='{hashes.Crc}' md5='{hashes.Md5}' sha1='{hashes.Sha1}'/></game></datafile>");
            var renamed = config with
            {
                DataDirectory = Path.Combine(root, "renamed-data"),
                Inputs = [config.Inputs[0] with
                { DatPath = renamedDat, DatSha256 = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(renamedDat))),
                    DatBinNames = new Dictionary<string, string> { ["track.bin"] = "catalog.bin" }, CatalogCueName = "catalog.cue" }]
            };
            File.WriteAllText(configuration, JsonSerializer.Serialize(renamed));
            string renamedReport = Path.Combine(root, "renamed-report.json");
            var measurementsOnly = await ChdCdQualificationChecks.RunAsync(["--chd-cd-qualification-probe", configuration, renamedReport], ChdCdQualificationChecks.Retention.MeasurementsOnly, [], CancellationToken.None);
            measurementsOnly.Archives.ShouldBeEmpty();
            Directory.Exists(Path.Combine(renamed.DataDirectory, "qualified-archives")).ShouldBeFalse();
            using var renamedResult = JsonDocument.Parse(File.ReadAllBytes(renamedReport));
            var renamedRow = renamedResult.RootElement.GetProperty("Rows")[0];
            renamedResult.RootElement.GetProperty("Status").GetString().ShouldBe("passed");
            renamedRow.GetProperty("DatMatchedMembers").EnumerateArray().Select(member => member.GetString()).ShouldBe(["catalog.bin"]);
            renamedRow.GetProperty("CatalogCueReference").GetProperty("Name").GetString().ShouldBe("catalog.cue");
            renamedRow.GetProperty("Originals").EnumerateArray().Single(member => member.GetProperty("Name").GetString() == "original.cue")
                .GetProperty("Digests").GetProperty("Sha256").GetString().ShouldBe(cueHashes.Sha256);
            (await Digests.FileAsync(Path.Combine(fixtures, "original.cue"))).ShouldBe(cueHashes);
            // A renamed descriptor cannot hide a changed BIN or append a success receipt.
            byte[] changed = File.ReadAllBytes(bin);
            changed[^1] ^= 1;
            File.WriteAllBytes(bin, changed);
            File.WriteAllText(configuration, JsonSerializer.Serialize(renamed with { DataDirectory = Path.Combine(root, "wrong-bin-data") }));
            string wrongBinReport = Path.Combine(root, "wrong-bin.json");
            await Should.ThrowAsync<InvalidOperationException>(() => ChdCdQualificationChecks.RunAsync(
                ["--chd-cd-qualification-probe", configuration, wrongBinReport], ChdCdQualificationChecks.Retention.MeasurementsOnly, [], CancellationToken.None));
            using var wrongBinResult = JsonDocument.Parse(File.ReadAllBytes(wrongBinReport));
            wrongBinResult.RootElement.GetProperty("Status").GetString().ShouldBe("failed");
            wrongBinResult.RootElement.GetProperty("Rows").GetArrayLength().ShouldBe(0);
            changed[^1] ^= 1;
            File.WriteAllBytes(bin, changed);
            // A bad independent oracle fails before staging and cannot append another success row.
            File.WriteAllText(configuration, JsonSerializer.Serialize(config with
            {
                DataDirectory = Path.Combine(root, "rejected-data"),
                Inputs = [config.Inputs[0] with { DatSha256 = new string('f', 64) }]
            }));
            string rejected = Path.Combine(root, "rejected.json");
            await Should.ThrowAsync<InvalidOperationException>(() => ChdCdQualificationChecks.RunAsync(
                ["--chd-cd-qualification-probe", configuration, rejected], ChdCdQualificationChecks.Retention.MeasurementsOnly, [], CancellationToken.None));
            using var failure = JsonDocument.Parse(File.ReadAllBytes(rejected));
            failure.RootElement.GetProperty("Status").GetString().ShouldBe("failed");
            failure.RootElement.GetProperty("Rows").GetArrayLength().ShouldBe(0);
            Directory.Exists(Path.Combine(root, "rejected-data")).ShouldBeFalse();
            (await Digests.FileAsync(bin)).ShouldBe(hashes);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("missing")]
    [InlineData("duplicate")]
    [InlineData("null")]
    public void Parse_AmbiguousConfiguration_Rejects(string mutation)
    {
        string json = JsonSerializer.Serialize(Config);
        json = mutation switch
        {
            "unknown" => json.Insert(1, "\"Unexpected\":true,"),
            "missing" => json.Replace("\"FixtureKind\":\"real\",", "", StringComparison.Ordinal),
            "duplicate" => json.Insert(1, "\"FixtureKind\":\"synthetic\","),
            _ => json.Replace("\"Sidecars\":[]", "\"Sidecars\":null", StringComparison.Ordinal)
        };
        Should.Throw<Exception>(() => ChdCdQualificationInputs.Parse(Encoding.UTF8.GetBytes(json)));
    }

    [Theory]
    [InlineData("unknown-scope")]
    [InlineData("duplicate-name")]
    [InlineData("traversal")]
    [InlineData("sidecar-collision")]
    [InlineData("null-input")]
    [InlineData("empty-inputs")]
    [InlineData("unbounded-output")]
    [InlineData("missing-dat-pin")]
    [InlineData("traversal-binding")]
    [InlineData("case-collision-binding")]
    [InlineData("duplicate-catalog-binding")]
    [InlineData("invalid-cue-reference")]
    [InlineData("original-cue-reference")]
    public void Validate_InvalidScopeOrFixture_Rejects(string mutation)
    {
        var config = mutation switch
        {
            "unknown-scope" => Config with { FixtureKind = "qualified" },
            "duplicate-name" => Config with { Inputs = [Input, Input with { Name = "FIXTURE" }] },
            "traversal" => Config with { Inputs = [Input with { CueName = "../original.cue" }] },
            "sidecar-collision" => Config with { Inputs = [Input with { Sidecars = ["ORIGINAL.CUE"] }] },
            "null-input" => Config with { Inputs = [null!] },
            "empty-inputs" => Config with { Inputs = [] },
            "unbounded-output" => Config with { Inputs = [Input with { MaximumOutputBytes = long.MaxValue }] },
            "traversal-binding" => Config with { Inputs = [Input with { DatBinNames = new Dictionary<string, string> { ["track.bin"] = "../catalog.bin" } }] },
            "case-collision-binding" => Config with { Inputs = [Input with { DatBinNames = new Dictionary<string, string> { ["track.bin"] = "a.bin", ["TRACK.BIN"] = "b.bin" } }] },
            "duplicate-catalog-binding" => Config with { Inputs = [Input with { DatBinNames = new Dictionary<string, string> { ["a.bin"] = "track.bin", ["b.bin"] = "TRACK.BIN" } }] },
            "invalid-cue-reference" => Config with { Inputs = [Input with { CatalogCueName = "track.bin" }] },
            "original-cue-reference" => Config with { Inputs = [Input with { CatalogCueName = "ORIGINAL.CUE" }] },
            _ => Config with { Inputs = [Input with { DatSha256 = "" }] }
        };
        Should.Throw<InvalidOperationException>(() => ChdCdQualificationInputs.Validate(config));
    }

    [Fact]
    public void ReadDat_PublicDoctypeAndMultipleMembers_MatchesExactNamedGameWithoutFetching()
    {
        var members = Read("<!DOCTYPE datafile SYSTEM 'https://invalid.example/no-fetch.dtd'><datafile><game name='Other'/><game name='Game'>" + Rom() + Rom("audio.bin") + "</game></datafile>");
        members.Count.ShouldBe(2);
        ChdCdQualificationInputs.MatchDat(new(2352, "00000000", new string('a', 32), new string('b', 40), new string('c', 64)), members["track.bin"]);
    }

    [Theory]
    [InlineData("duplicate-game")]
    [InlineData("missing-game")]
    [InlineData("duplicate-file")]
    [InlineData("zero-size")]
    [InlineData("missing-digest")]
    [InlineData("traversal")]
    [InlineData("entity")]
    public void ReadDat_IncompleteOrAmbiguousOracle_Rejects(string mutation)
    {
        string rom = Rom();
        string xml = mutation switch
        {
            "duplicate-game" => $"<datafile><game name='Game'>{rom}</game><game name='Game'>{rom}</game></datafile>",
            "missing-game" => $"<datafile><game name='Other'>{rom}</game></datafile>",
            "duplicate-file" => $"<datafile><game name='Game'>{rom}{Rom("TRACK.BIN")}</game></datafile>",
            "zero-size" => $"<datafile><game name='Game'>{rom.Replace("2352", "0", StringComparison.Ordinal)}</game></datafile>",
            "missing-digest" => $"<datafile><game name='Game'>{rom.Replace("crc='00000000'", "", StringComparison.Ordinal)}</game></datafile>",
            "traversal" => $"<datafile><game name='Game'>{Rom("../track.bin")}</game></datafile>",
            _ => "<!DOCTYPE datafile [<!ENTITY x 'Game'>]><datafile><game name='&x;'>" + rom + "</game></datafile>"
        };
        Should.Throw<Exception>(() => Read(xml));
    }

    [Theory]
    [InlineData("size")]
    [InlineData("crc")]
    [InlineData("md5")]
    [InlineData("sha1")]
    public void MatchDat_AnySingleMismatch_Rejects(string field)
    {
        var expected = new ChdCdQualificationInputs.DatMember(2352, "00000000", new string('a', 32), new string('b', 40));
        var actual = new Digests(2352, "00000000", new string('a', 32), new string('b', 40), new string('c', 64));
        actual = field switch
        {
            "size" => actual with { Size = 4704 },
            "crc" => actual with { Crc = "12345678" },
            "md5" => actual with { Md5 = new string('d', 32) },
            _ => actual with { Sha1 = new string('d', 40) }
        };
        Should.Throw<InvalidOperationException>(() => ChdCdQualificationInputs.MatchDat(actual, expected));
    }

    [Theory]
    [InlineData("extra-dat-bin")]
    [InlineData("unmatched-cue-bin")]
    [InlineData("changed-dat")]
    [InlineData("sidecar-bin-collision")]
    [InlineData("unknown-descriptor")]
    [InlineData("partial-sector")]
    public void ReadLayout_PartialSetOrChangedOracle_Rejects(string mutation)
    {
        string root = Path.Combine(Path.GetTempPath(), "romd-cd-oracle-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string datPath = Path.Combine(root, "oracle.dat");
            string cueName = mutation == "unmatched-cue-bin" ? "other.bin" : "track.bin";
            File.WriteAllText(Path.Combine(root, "original.cue"), $"FILE \"{cueName}\" BINARY\n TRACK 01 MODE2/2352\n INDEX 01 00:00:00\n");
            File.WriteAllText(datPath, "<datafile><game name='Game'>" + Rom() + (mutation == "extra-dat-bin" ? Rom("audio.bin") : "") + "</game></datafile>");
            if (mutation == "unknown-descriptor")
                File.WriteAllText(datPath, "<datafile><game name='Game'>" + Rom() + Rom("other.cue") + "</game></datafile>");
            if (mutation == "partial-sector")
                File.WriteAllText(datPath, "<datafile><game name='Game'>" + Rom().Replace("2352", "2351", StringComparison.Ordinal) + "</game></datafile>");
            string pin = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(datPath)));
            var input = Input with
            {
                FixtureDirectory = root,
                DatPath = datPath,
                DatSha256 = mutation == "changed-dat" ? new string('f', 64) : pin,
                Sidecars = mutation == "sidecar-bin-collision" ? ["TRACK.BIN"] : []
            };
            Should.Throw<InvalidOperationException>(() => ChdCdQualificationInputs.ReadLayout(input));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Theory]
    [InlineData("incomplete-map")]
    [InlineData("duplicate-target")]
    [InlineData("unknown-target")]
    [InlineData("extra-source")]
    [InlineData("missing-cue-reference")]
    [InlineData("extra-dat-bin")]
    [InlineData("cue-hides-bin")]
    [InlineData("reference-is-original")]
    public void ReadLayout_InvalidExplicitBinding_RejectsWithoutChangingOriginalCue(string mutation)
    {
        string root = Path.Combine(Path.GetTempPath(), "romd-cd-bindings-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string cue = "FILE \"first.bin\" BINARY\n TRACK 01 MODE2/2352\n INDEX 01 00:00:00\nFILE \"second.bin\" BINARY\n TRACK 02 AUDIO\n INDEX 01 00:00:00\n";
            File.WriteAllText(Path.Combine(root, "original.cue"), cue);
            string dat = Path.Combine(root, "oracle.dat");
            File.WriteAllText(dat, "<datafile><game name='Game'>" + Rom("catalog1.bin") + Rom("catalog2.bin") +
                Rom("catalog.cue").Replace("2352", "100", StringComparison.Ordinal) + (mutation == "extra-dat-bin" ? Rom("extra.bin") : "") + "</game></datafile>");
            Dictionary<string, string> bindings = new() { ["first.bin"] = "catalog1.bin", ["second.bin"] = "catalog2.bin" };
            if (mutation == "incomplete-map")
                bindings.Remove("second.bin");
            if (mutation == "duplicate-target")
                bindings["second.bin"] = "catalog1.bin";
            if (mutation == "unknown-target")
                bindings["second.bin"] = "unknown.bin";
            if (mutation == "extra-source")
                bindings.Add("extra-source.bin", "catalog2.bin");
            var input = Input with
            {
                FixtureDirectory = root,
                DatPath = dat,
                DatSha256 = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(dat))),
                DatBinNames = bindings,
                CatalogCueName = mutation switch
                {
                    "missing-cue-reference" => null,
                    "cue-hides-bin" => "catalog2.bin",
                    "reference-is-original" => "original.cue",
                    _ => "catalog.cue"
                }
            };
            Should.Throw<InvalidOperationException>(() => ChdCdQualificationInputs.ReadLayout(input));
            File.ReadAllText(Path.Combine(root, "original.cue")).ShouldBe(cue);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Theory]
    [InlineData("report-in-fixtures")]
    [InlineData("data-in-fixtures")]
    [InlineData("report-in-data")]
    [InlineData("existing-report")]
    [InlineData("symlink-report")]
    public void ValidatePaths_DestructiveAliases_RejectsWithoutChangingInputs(string mutation)
    {
        string root = Path.Combine(Path.GetTempPath(), "romd-cd-paths-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string fixtures = Path.Combine(root, "fixtures");
            Directory.CreateDirectory(fixtures);
            string original = Path.Combine(fixtures, "original.cue");
            File.WriteAllText(original, "original bytes");
            var config = Config with
            {
                NativeDirectory = Path.Combine(root, "native"),
                ToolDirectory = Path.Combine(root, "tool"),
                DataDirectory = Path.Combine(root, "new-data"),
                Inputs = [Input with { FixtureDirectory = fixtures, DatPath = Path.Combine(root, "oracle.dat") }]
            };
            string report = Path.Combine(root, "report.json");
            if (mutation == "report-in-fixtures")
                report = original;
            if (mutation == "data-in-fixtures")
                config = config with { DataDirectory = Path.Combine(fixtures, "data") };
            if (mutation == "report-in-data")
                report = Path.Combine(config.DataDirectory, "report.json");
            if (mutation == "existing-report")
                File.WriteAllText(report, "existing report");
            if (mutation == "symlink-report")
                File.CreateSymbolicLink(report, original);
            Should.Throw<InvalidOperationException>(() => ChdCdQualificationInputs.ValidatePaths(config, Path.Combine(root, "config.json"), report));
            File.ReadAllText(original).ShouldBe("original bytes");
            if (mutation == "existing-report")
                File.ReadAllText(report).ShouldBe("existing report");
            Directory.Exists(config.DataDirectory).ShouldBeFalse();
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
