using System.IO.Abstractions;
using NSubstitute;
using Romd.Admin.Application.Source.Rom;
using Romd.Admin.Application.Source.Rom.Compressed;
using Romd.Admin.Application.Storage.Files;
using Romd.Application.Common.Configuration;
using Romd.Domain.Source.Rom;
using Romd.Infrastructure.Hashing;
using Romd.Infrastructure.Storage;
using Romd.Media.Cd;
using Romd.Media.Chd;
using Romd.Media.TestSupport;
using Romd.PostgreSql.TestSupport;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Storage;

/// <summary>Actual supervised native observations compared with independent synthetic track bytes; no original file grouping is inferred.</summary>
public sealed class ChdCdImportPreparationTests : IDisposable
{
    private readonly string root = Directory.CreateTempSubdirectory("romd-cd-import-preparation-").FullName;

    [Theory]
    [InlineData("single", false, false)]
    [InlineData("single", false, true)]
    [InlineData("single", true, false)]
    [InlineData("single", true, true)]
    [InlineData("multi", false, false)]
    [InlineData("multi", false, true)]
    [InlineData("multi", true, false)]
    [InlineData("multi", true, true)]
    public async Task Prepare_ActualCd_CarriesRawIdentityAndEveryMeasuredTrackUnderStoppedCustody(string shape, bool admitted, bool exactCue)
    {
        var options = new MediaProcessingOptions { MaxConcurrent = 1, AdmissionTimeoutSeconds = 1 };
        var storage = Substitute.For<IRomdOptions>();
        storage.DataDirectory.Returns(root);
        storage.MaxUploadBytes.Returns(2L << 30);
        var runtime = new MediaProcessingRuntime(storage, options);
        var factory = new LocalTempFileFactory(storage, new FileSystem());
        var native = ChdNativeCandidate.FromBuildRecord(NativeTestBuilds.LibchdrDirectory);
        var supervisor = new MediaWorkerSupervisor(options, runtime, native);
        var inspector = new ChdCdImportInspector(options, supervisor, runtime, native, TimeProvider.System);
        var adapter = new ChdPreparationAdapter(() => throw new InvalidOperationException("A measured CD cannot enter DVD inspection."), () => inspector);
        await using var input = File.OpenRead(NativeTestBuilds.LibchdrFixture("cd-edge-" + shape + ".chd"));
        byte[] prefix = new byte[124];
        await input.ReadExactlyAsync(prefix);
        input.Position = 0;
        using var admission = admitted ? (await adapter.AdmitAsync(prefix, default)).ShouldNotBeNull() : null;
        if (admitted)
        {
            using var blocked = new CancellationTokenSource();
            var waiting = runtime.AcquireAsync(ct: blocked.Token);
            blocked.Cancel();
            await Should.ThrowAsync<OperationCanceledException>(() => waiting);
        }
        await using var staged = await factory.CreateAsync(input,
            includeDatHashes: bytes => adapter.RequiresDatHashes(bytes.Span),
            selectPrefixLength: bytes => RomPreparationDetection.SelectPrefixLength(bytes, "renamed.rvz"));

        var result = admission is null ? await adapter.VerifyAsync(staged, "renamed.rvz", default)
            : await admission.PrepareAsync(staged, "renamed.rvz");

        result.IsError.ShouldBeFalse(result.IsError ? string.Join(';', result.Errors) : "");
        await using var prepared = result.Value.ShouldBeOfType<PreparedRom.Cd>();
        prepared.Artifact.ShouldBeSameAs(staged);
        staged.DatHashes.ShouldNotBeNull();
        var observed = prepared.Inspection;
        observed.ArtifactIdentity.Sha256.ShouldBe(staged.FileSha256);
        observed.ArtifactIdentity.Size.ShouldBe(input.Length);
        observed.ArtifactIdentity.Sha1.ShouldBe(staged.DatHashes.Sha1);
        observed.ArtifactIdentity.Md5.ShouldBe(staged.DatHashes.Md5);
        observed.ArtifactIdentity.Crc32.ShouldBe(staged.DatHashes.Crc32);
        observed.Decoder.Rid.ShouldBe(native.ExpectedIdentity.Rid);
        observed.Decoder.RecipeVersion.ShouldBe(native.ExpectedIdentity.RecipeVersion);
        observed.CheckedProfile.ShouldBe(ChdCdInspectionService.Profile);
        observed.ProjectionProfile.ShouldBe(ChdCdInspectionService.ProjectionProfile);
        observed.Tracks.Count.ShouldBe(3);
        observed.Boot.ShouldBeNull();
        for (int index = 0; index < 3; index++)
            observed.Tracks[index].Identity.ShouldBe(MediaFactFixtures.Identity(File.ReadAllBytes(NativeTestBuilds.LibchdrFixture($"cd-edge-track{index + 1}.bin"))));
        observed.Tracks[0].Placement.Kind.ShouldBe(RomCdTrackKind.Mode2Raw);
        observed.Tracks[0].AlignmentFrames.ShouldBe(3);
        observed.Tracks[1].Placement.FirstFrame.ShouldBe(20);
        observed.Tracks[1].PregapStored.ShouldBeTrue();
        observed.Tracks[1].PregapFrames.ShouldBe(2);
        observed.Tracks[2].PregapStored.ShouldBeFalse();
        observed.Tracks[2].PregapFrames.ShouldBe(2);
        var names = Enumerable.Range(1, 3).Select(number => $"track{number}.bin").ToArray();
        var generatedResult = await new RomCdCueGenerator(factory).GenerateAsync(observed.Tracks, names);
        generatedResult.IsError.ShouldBeFalse();
        await using var generated = generatedResult.Value;
        var expectedCue = exactCue ? generated.Identity : MediaFactFixtures.Identity("REM independent preservation requirement\r\n"u8);
        var cueRequirement = new CatalogDescriptorRequirement(7, 11, "release-v1", "cue-v1", "disc.cue", expectedCue.Size,
            expectedCue.Sha1, expectedCue.Md5, expectedCue.Crc32);
        var bins = observed.Tracks.Select((track, index) => new CatalogCdBinBinding(
            new(20 + index, $"bin-{index}", names[index], track.Identity.Size, track.Identity.Sha1, track.Identity.Md5, track.Identity.Crc32, false),
            new(track.Placement.Number, track.Identity))).ToArray();
        var binding = new RomCdImportBinding(13, 17, cueRequirement, bins, "psx");
        PreparedRom.WholeFile? original = null;
        if (exactCue)
        {
            var ordinary = MediaInfrastructureTestServices.ManagedPreparation(new HashingService(),
                () => throw new InvalidOperationException("A generated CUE cannot enter RVZ verification."), new MediaInfrastructureTestServices.EmptyReferences());
            var verified = await ordinary.PrepareAsync(generated.Artifact, cueRequirement.Path);
            verified.IsError.ShouldBeFalse();
            original = verified.Value.ShouldBeOfType<PreparedRom.WholeFile>();
            Should.Throw<ArgumentException>(() => new RomCdImportPlan(prepared, binding, generated, originalCue: null));
        }
        await using var originalCustody = original;
        var plan = new RomCdImportPlan(prepared, binding, generated, original);

        plan.ExactCue.ShouldBe(exactCue);
        plan.OriginalCue.ShouldBeSameAs(original);
        if (exactCue)
            plan.OriginalCue.ShouldNotBeNull().Verification.Identity.ShouldBe(generated.Identity);
        plan.Binding.Cue.ShouldBe(cueRequirement);
        plan.Measured.Measurement.Id.ShouldBe(observed.Id);
        plan.Measured.Measurement.CheckedProfile.ShouldBe(observed.CheckedProfile);
        plan.Measured.Measurement.Decoder.ShouldBe(new RomDecoderIdentity.Native(observed.Decoder));
        plan.Measured.Measurement.ObservedAt.ShouldBe(observed.ObservedAt);
        plan.Measured.Measurement.Outputs.Select(output => output.Identity).ShouldBe(observed.Tracks.Select(track => track.Identity));
        var manifest = plan.Measured.Manifest.ShouldBeOfType<RomReconstructionManifest.CdTracks>();
        manifest.Members[plan.CueOrdinal].Identity.Sha256.ShouldBe(generated.Identity.Sha256);
        manifest.Members[plan.CueOrdinal].Identity.Size.ShouldBe(generated.Identity.Size);
        var wrong = bins.ToArray();
        wrong[0] = wrong[0] with { Requirement = wrong[0].Requirement with { Size = wrong[0].Requirement.Size + 1 } };
        Should.Throw<ArgumentException>(() => new RomCdImportPlan(prepared, binding with { Bins = wrong }, generated, original));
        await prepared.DisposeAsync();
        File.Exists(staged.Path).ShouldBeTrue();
        using var recovered = await runtime.AcquireAsync();
        recovered.Released.ShouldBeFalse();
    }

    public void Dispose() => Directory.Delete(root, true);
}
